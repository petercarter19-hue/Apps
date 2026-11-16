using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Spotlight;

/// <summary>
/// The quick bar: a small floating glass pill (switch, how dark, a drop-down) that can be dragged anywhere, with a panel for the common
/// settings, in a dark or a light look. One per-pixel-alpha window that draws everything itself and never takes focus, so the light
/// stays on the window you were using. It sits above the dimming, so it is always lit. Lives in the tray copy.
/// </summary>
internal sealed class QuickBarWindow : NativeWindow, IDisposable
{
    // ---- what the bar asks for (the tray copy does it) ----
    public event Action? ToggleRequested, PauseRequested, SettingsRequested, HideRequested, PickAreaRequested, FollowRequested, DimCommitted;
    public event Action<int>? DimPreview;
    public event Action<string>? TintChosen;
    public event Action<int, int>? Moved;

    // ---- look ----
    private sealed record Palette(
        Color Tint, Color TintPanel, Color Hairline, Color RimTop, Color RimBottom, Color Sheen, Color Text, Color Text2, Color Muted,
        Color Accent1, Color Accent2, Color Glow, Color DotRing, Color Hover, Color Divider, Color Track, Color Groove, Color Knob,
        (int Dy, int Blur, float Alpha)[] Float);

    private static Color A(int r, int g, int b, double a) => Color.FromArgb((int)Math.Round(a * 255), r, g, b);

    private static readonly Palette Dark = new(
        // The tints are a little firmer than the mockups: Windows cannot blur what is behind a layered window the way the pictures did,
        // so a firmer tint keeps text behind the bar from showing through.
        A(28, 30, 34, .84), A(28, 30, 34, .92), A(0, 0, 0, .50), A(255, 255, 255, .42), A(255, 255, 255, .06), A(255, 255, 255, .06),
        Color.FromArgb(0xF3, 0xF3, 0xF3), Color.FromArgb(0xE2, 0xE2, 0xE2), Color.FromArgb(0xA9, 0xAC, 0xB2),
        Color.FromArgb(0xDA, 0x88, 0x36), Color.FromArgb(0xC4, 0x6F, 0x1A), A(224, 140, 52, .35), A(255, 255, 255, .62), A(255, 255, 255, .07),
        A(255, 255, 255, .10), A(255, 255, 255, .22), A(255, 255, 255, .20), Color.White,
        new[] { (3, 4, .55f), (12, 24, .30f), (22, 50, .50f) });

    private static readonly Palette Light = new(
        A(255, 255, 255, .88), A(255, 255, 255, .96), A(0, 0, 0, .10), A(255, 255, 255, 1), A(255, 255, 255, .55), A(255, 255, 255, .22),
        Color.FromArgb(0x14, 0x14, 0x14), Color.FromArgb(0x3A, 0x3A, 0x3A), Color.FromArgb(0x55, 0x58, 0x5E),
        Color.FromArgb(0xC8, 0x78, 0x2A), Color.FromArgb(0xB4, 0x62, 0x0E), A(0, 0, 0, 0), A(0, 0, 0, .22), A(0, 0, 0, .05),
        A(0, 0, 0, .09), A(0, 0, 0, .16), A(0, 0, 0, .22), Color.White,
        new[] { (2, 4, .45f), (10, 22, .20f), (24, 56, .42f) });

    private static readonly Color[] Swatch =
    {
        Color.FromArgb(0x14, 0x14, 0x14), Color.FromArgb(0x7A, 0x48, 0x18), Color.FromArgb(0x26, 0x4A, 0x86),
        Color.FromArgb(0x29, 0x65, 0x43), Color.FromArgb(0x56, 0x37, 0x8A),
    };

    // ---- state ----
    private Box _pill;               // screen pixels
    private Box _work;               // work area of the screen the pill is on
    private double _scale = 1;
    private bool _open, _wanted, _hiddenForFullScreen, _shownNow, _enabled = true, _paused, _areaMode;
    private int _dim = Strengths.Default;
    private string _tint = "Black", _theme = "Dark";
    private string? _hover;
    private Point _pressAt; private Box _pressPill; private string? _pressTarget; private bool _dragging, _sliding;
    private readonly System.Windows.Forms.Timer _outside = new() { Interval = 90 };
    private readonly System.Windows.Forms.Timer _fullScreenWatch = new() { Interval = 600 };
    private readonly Dictionary<string, Bitmap> _shadowCache = new();
    private string _iconFont = "Segoe MDL2 Assets";
    private bool _hideFromCapture;
    private Box _winRect;        // the control window: just the pill (and the open panel)
    private Box _shadowRect;     // the picture window behind it that holds the soft shadow (click-through)
    private readonly ShadowWindow _shadow = new();

    /// <summary>The soft shadow lives in its own click-through window (a click in the shadow must reach the window under it).</summary>
    private sealed class ShadowWindow : NativeWindow
    {
        public ShadowWindow() => CreateHandle(new CreateParams
        {
            Caption = "Spotlight quick bar shadow", Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            X = -32000, Y = -32000, Width = 10, Height = 10,
        });
    }

    public QuickBarWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Spotlight quick bar",
            Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            X = -32000, Y = -32000, Width = 10, Height = 10,
        });
        try { _iconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets"; } catch { }
        _outside.Tick += (_, _) => CheckOutsideClick();
        _fullScreenWatch.Tick += (_, _) => SetFullScreenFront(ForegroundIsFullScreen());
        _fullScreenWatch.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    /// <summary>Where the pill is on the screen right now (the settings window opens beneath it).</summary>
    public Box PillBox => _pill;

    // ---- told by the tray copy ----

    /// <summary>The settings and state changed (or the bar was asked to show or hide). Redraws.</summary>
    public void Update(Settings s, bool paused)
    {
        _theme = QuickBarLayout.NormalizeTheme(s.QuickBarTheme);
        _enabled = s.Enabled; _paused = paused; _tint = s.Tint; _areaMode = s.AreaMode;
        if (!_sliding) _dim = s.DimPercent;
        _wanted = s.QuickBar;
        if (s.HideFromCapture != _hideFromCapture)
        {
            _hideFromCapture = s.HideFromCapture; // people watching a shared screen or a recording see no pill either
            Native.SetWindowDisplayAffinity(Handle, _hideFromCapture ? 0x11u : 0u); Native.SetWindowDisplayAffinity(_shadow.Handle, _hideFromCapture ? 0x11u : 0u);
        }
        if (!_dragging)
        {
            if (s.QuickBarX is { } x && s.QuickBarY is { } y) _pill = new Box(x, y, x + 1, y + 1);
            else _pill = default;
            Relocate(initial: true);
        }
        Redraw();
    }

    public void SetFullScreenFront(bool on) { if (_hiddenForFullScreen == on) return; _hiddenForFullScreen = on; Redraw(); }

    /// <summary>
    /// Is a full-screen window (a game, a video) in front? Looked at a couple of times a second, on its own, so it works whether dimming is on,
    /// off, paused or showing a picked area. Only native calls: the front window covers a whole screen and has no title bar.
    /// </summary>
    private bool ForegroundIsFullScreen()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle || fg == _shadow.Handle || !Native.IsWindowVisible(fg) || Native.IsIconic(fg)) return false;
        var cls = new System.Text.StringBuilder(64); Native.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if ((Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64() & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var box = new Box(r.Left, r.Top, r.Right, r.Bottom);
        foreach (var m in Win.Monitors()) if (box.Covers(m.Bounds)) return true;
        return false;
    }

    public void RaiseToTop()
    {
        if (_shownNow)
        {
            Native.SetWindowPos(_shadow.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); // the controls above their shadow
        }
    }

    private void OnDisplayChanged(object? s, EventArgs e) { try { Relocate(initial: false); Redraw(); } catch { } }

    // ---- where it is ----

    private static List<Box> Screens() => Win.Monitors().Select(m => m.Bounds).ToList();

    private (Box Work, double Scale, Box All) ScreenOf(Box pill)
    {
        var mons = Win.Monitors();
        if (mons.Count == 0) return (new Box(0, 0, 1920, 1040), 1, new Box(0, 0, 1920, 1080));
        int cx = (pill.Left + pill.Right) / 2, cy = (pill.Top + pill.Bottom) / 2;
        var m = mons.FirstOrDefault(x => cx >= x.Bounds.Left && cx < x.Bounds.Right && cy >= x.Bounds.Top && cy < x.Bounds.Bottom) ?? mons[0];
        var wa = Screen.AllScreens.FirstOrDefault(sc => sc.Bounds.Left == m.Bounds.Left && sc.Bounds.Top == m.Bounds.Top)?.WorkingArea ?? new Rectangle(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height);
        var all = new Box(mons.Min(x => x.Bounds.Left), mons.Min(x => x.Bounds.Top), mons.Max(x => x.Bounds.Right), mons.Max(x => x.Bounds.Bottom));
        return (new Box(wa.Left, wa.Top, wa.Right, wa.Bottom), m.Scale, all);
    }

    /// <summary>
    /// Sizes the pill for the screen it ends up on: size and clamp it, look at which screen that is, and repeat if that screen has another
    /// scale. Returns the pill with the work area and scale it was sized for (keeping a first screen's values would leave a pill, and
    /// its panel, sized and placed for another screen).
    /// </summary>
    private Box Fit(Box pill, out Box work, out double scale)
    {
        (work, scale, _) = ScreenOf(pill);
        for (int i = 0; i < 3; i++)
        {
            var next = QuickBarLayout.ClampToScreens(QuickBarLayout.PillRect(pill.Left, pill.Top, scale), Screens());
            var (work2, scale2, _) = ScreenOf(next);
            next = QuickBarLayout.Clamp(next, work2); // and out of the taskbar strip: the usable area of that screen
            pill = next; work = work2; // the pill is now sized for 'scale'
            if (Math.Abs(scale2 - scale) < 1e-6) break;
            if (i < 2) scale = scale2; // another screen's scale: size it again for that screen (the last pass keeps the size it has)
        }
        return pill;
    }

    private void Relocate(bool initial)
    {
        if (_pill.IsEmpty && _pill.Left == 0 && _pill.Right == 0)
        {
            var prim = ScreenOf(new Box(10, 10, 11, 11));
            _pill = QuickBarLayout.DefaultPill(prim.Work, prim.Scale);
        }
        _pill = Fit(_pill, out _work, out _scale);
    }

    // ---- drawing ----

    private Box PanelBoxNow() => QuickBarLayout.PanelBox(_pill, _work, _scale);

    private void Redraw()
    {
        bool show = _wanted && !_hiddenForFullScreen;
        if (!show)
        {
            if (_shownNow) { Native.ShowWindow(Handle, Native.SW_HIDE); Native.ShowWindow(_shadow.Handle, Native.SW_HIDE); _shownNow = false; _outside.Stop(); }
            return;
        }
        var (shadowBmp, bodyBmp) = ComposeAll();
        using (shadowBmp) using (bodyBmp)
        {
            Present(_shadow.Handle, shadowBmp, _shadowRect);
            ApplyRegion();
            Present(Handle, bodyBmp, _winRect);
            if (!_shownNow)
            {
                Native.SetWindowPos(_shadow.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                _shownNow = true;
            }
        }
        if (_open && !_outside.Enabled) _outside.Start();
        if (!_open) _outside.Stop();
    }

    /// <summary>Draws both pictures: the soft shadow (big, click-through) and the controls (pill and open panel). Also used by the picture tests, which never show them.</summary>
    internal (Bitmap Shadow, Bitmap Body) ComposeAll()
    {
        var pal = _theme == "Light" ? Light : Dark;
        Box? panel = _open ? PanelBoxNow() : null;
        _shadowRect = QuickBarLayout.WindowRect(_pill, panel, _scale);
        _winRect = panel is { } pn ? new Box(Math.Min(_pill.Left, pn.Left), Math.Min(_pill.Top, pn.Top), Math.Max(_pill.Right, pn.Right), Math.Max(_pill.Bottom, pn.Bottom)) : _pill;
        var shadowBmp = new Bitmap(_shadowRect.Width, _shadowRect.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(shadowBmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawShadow(g, pal, Local(_pill, _shadowRect), Math.Min(_pill.Width, _pill.Height) / 2, "pill");
            if (panel is { } p) DrawShadow(g, pal, Local(p, _shadowRect), QuickBarLayout.S(18, _scale), "panel");
        }
        var bodyBmp = new Bitmap(_winRect.Width, _winRect.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bodyBmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var pr = Local(_pill, _winRect);
            DrawBody(g, pal, pr, pr.Height / 2, pal.Tint);
            DrawPill(g, pal, pr);
            if (panel is { } p)
            {
                var pb = Local(p, _winRect);
                DrawBody(g, pal, pb, QuickBarLayout.S(18, _scale), pal.TintPanel);
                DrawPanel(g, pal, pb);
            }
        }
        return (shadowBmp, bodyBmp);
    }

    /// <summary>The control window is shaped like the pill (and the panel): everything else, the gap between them included, is not part of it, so clicks go through.</summary>
    private void ApplyRegion()
    {
        var pr = Local(_pill, _winRect);
        IntPtr rgn = Native.CreateRoundRectRgn(pr.Left, pr.Top, pr.Right + 1, pr.Bottom + 1, pr.Height, pr.Height);
        if (_open)
        {
            var pb = Local(PanelBoxNow(), _winRect); int d = QuickBarLayout.S(18, _scale) * 2;
            IntPtr prgn = Native.CreateRoundRectRgn(pb.Left, pb.Top, pb.Right + 1, pb.Bottom + 1, d, d);
            Native.CombineRgn(rgn, rgn, prgn, 2 /* RGN_OR */);
            Native.DeleteObject(prgn);
        }
        Native.SetWindowRgn(Handle, rgn, false); // Windows owns the region from here
    }

    private static Rectangle Local(Box b, Box origin) => new(b.Left - origin.Left, b.Top - origin.Top, b.Width, b.Height);

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }

    private void DrawShadow(Graphics g, Palette pal, Rectangle body, int radius, string kind)
    {
        string key = $"{_theme}|{kind}|{body.Width}x{body.Height}|{_scale:0.00}";
        if (!_shadowCache.TryGetValue(key, out var sh))
        {
            int mx = QuickBarLayout.S(QuickBarLayout.ShadowSide, _scale), mt = QuickBarLayout.S(QuickBarLayout.ShadowTop, _scale), mb = QuickBarLayout.S(QuickBarLayout.ShadowBottom, _scale);
            sh = BuildShadow(body.Width + 2 * mx, body.Height + mt + mb, new Rectangle(mx, mt, body.Width, body.Height), radius, pal.Float, _scale);
            if (_shadowCache.Count > 12) { foreach (var b in _shadowCache.Values) b.Dispose(); _shadowCache.Clear(); }
            _shadowCache[key] = sh;
        }
        int ox = QuickBarLayout.S(QuickBarLayout.ShadowSide, _scale), oy = QuickBarLayout.S(QuickBarLayout.ShadowTop, _scale);
        g.DrawImage(sh, new Rectangle(body.X - ox, body.Y - oy, sh.Width, sh.Height));
    }

    /// <summary>A soft shadow picture (Gaussian-like, from three box blurs) made at half size and drawn at full size.</summary>
    private static Bitmap BuildShadow(int w, int h, Rectangle body, int radius, (int Dy, int Blur, float Alpha)[] layers, double scale)
    {
        int hw = Math.Max(2, w / 2), hh = Math.Max(2, h / 2);
        var total = new float[hw * hh]; // 1 - product of (1 - alpha)
        Array.Fill(total, 1f);
        foreach (var (dy, blur, alpha) in layers)
        {
            var mask = new float[hw * hh];
            int dyS = (int)Math.Round(dy * scale), blurS = (int)Math.Round(blur * scale);
            double r = radius / 2.0;
            double x0 = body.X / 2.0, y0 = (body.Y + dyS) / 2.0, x1 = body.Right / 2.0, y1 = (body.Bottom + dyS) / 2.0;
            for (int y = (int)Math.Max(0, y0 - 1); y < Math.Min(hh, y1 + 1); y++)
                for (int x = (int)Math.Max(0, x0 - 1); x < Math.Min(hw, x1 + 1); x++)
                {
                    double cx = x + .5, cy = y + .5;
                    double dx = Math.Max(Math.Max(x0 + r - cx, cx - (x1 - r)), 0), dyy = Math.Max(Math.Max(y0 + r - cy, cy - (y1 - r)), 0);
                    double dist = Math.Sqrt(dx * dx + dyy * dyy) - r;
                    mask[y * hw + x] = (float)Math.Clamp(.5 - dist, 0, 1);
                }
            double sigma = blurS / 2.0 / 2.0; // CSS blur radius = 2 sigma, then half size
            int br = Math.Max(1, (int)Math.Round(sigma * 1.2));
            for (int pass = 0; pass < 3; pass++) { BoxBlur(mask, hw, hh, br, true); BoxBlur(mask, hw, hh, br, false); }
            for (int i = 0; i < total.Length; i++) total[i] *= 1f - Math.Clamp(mask[i] * alpha, 0f, 1f);
        }
        var small = new Bitmap(hw, hh, PixelFormat.Format32bppPArgb);
        var data = small.LockBits(new Rectangle(0, 0, hw, hh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var px = new int[hw * hh];
        for (int i = 0; i < px.Length; i++) { int a = (int)Math.Round((1f - total[i]) * 255); px[i] = a << 24; } // black, premultiplied
        Marshal.Copy(px, 0, data.Scan0, px.Length); small.UnlockBits(data);
        var big = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(big)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(small, new Rectangle(0, 0, w, h)); }
        small.Dispose();
        return big;
    }

    private static void BoxBlur(float[] a, int w, int h, int r, bool horizontal)
    {
        var tmp = new float[Math.Max(w, h)];
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        for (int l = 0; l < lines; l++)
        {
            float sum = 0; int n = 2 * r + 1;
            for (int i = -r; i <= r; i++) sum += a[Idx(l, Math.Clamp(i, 0, len - 1), w, horizontal)];
            for (int i = 0; i < len; i++)
            {
                tmp[i] = sum / n;
                int add = Math.Min(len - 1, i + r + 1), sub = Math.Max(0, i - r);
                sum += a[Idx(l, add, w, horizontal)] - a[Idx(l, sub, w, horizontal)];
            }
            for (int i = 0; i < len; i++) a[Idx(l, i, w, horizontal)] = tmp[i];
        }
    }
    private static int Idx(int line, int i, int w, bool horizontal) => horizontal ? line * w + i : i * w + line;

    private void DrawBody(Graphics g, Palette pal, Rectangle r, int radius, Color tint)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        using (var path = Round(rf, radius))
        {
            using (var b = new SolidBrush(tint)) g.FillPath(b, path);
            // sheen: the top 55 percent catches a little light
            var sheenRect = new RectangleF(r.X, r.Y, r.Width, r.Height * .55f);
            g.SetClip(path);
            using (var sb = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, sheenRect.Height + 2), pal.Sheen, Color.FromArgb(0, pal.Sheen), LinearGradientMode.Vertical))
                g.FillRectangle(sb, sheenRect);
            g.ResetClip();
        }
        using (var outer = Round(new RectangleF(r.X - .5f, r.Y - .5f, r.Width + 1, r.Height + 1), radius + .5f))
        using (var pen = new Pen(pal.Hairline, 1f)) g.DrawPath(pen, outer);
        using (var rim = Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), radius - .5f))
        using (var pen = new Pen(new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height), pal.RimTop, pal.RimBottom, LinearGradientMode.Vertical), 1f)) g.DrawPath(pen, rim);
    }

    private Font F(double px, FontStyle style = FontStyle.Regular) => new("Segoe UI", (float)(px * _scale), style, GraphicsUnit.Pixel);
    private Font Icons(double px) => new(_iconFont, (float)(px * _scale), FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly StringFormat Center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    private static readonly StringFormat Left = new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
    private static readonly StringFormat Right = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

    private Rectangle Rel(Rectangle origin, Box part) => new(origin.X + part.Left, origin.Y + part.Top, part.Width, part.Height);

    private void DrawPill(Graphics g, Palette pal, Rectangle pill)
    {
        // hover wash
        if (_hover is "chevron" or "label")
        {
            var cr = Rel(pill, QuickBarLayout.Chevron(_scale));
            using var path = Round(cr, cr.Height / 2f); using var b = new SolidBrush(pal.Hover); g.FillPath(b, path);
        }
        // grip: six dots
        var grip = Rel(pill, QuickBarLayout.Grip(_scale));
        using (var db = new SolidBrush(_hover == "grip" ? pal.Text2 : A(pal.Muted.R, pal.Muted.G, pal.Muted.B, .75)))
        {
            float d = (float)(2.2 * _scale), gap = (float)(5.2 * _scale);
            float cx = grip.X + grip.Width / 2f, cy = grip.Y + grip.Height / 2f;
            for (int c = -1; c <= 0; c++) for (int r = -1; r <= 1; r++) g.FillEllipse(db, cx + c * gap + (c == -1 ? -d + .4f : .4f), cy + r * gap - d / 2, d, d);
        }
        DrawSwitch(g, pal, Rel(pill, QuickBarLayout.Switch(_scale)), _enabled && !_paused);
        using (var f = F(14, FontStyle.Bold)) using (var tb = new SolidBrush(pal.Text))
            g.DrawString(QuickBarLayout.StatusText(_enabled, _paused, _dim), f, tb, Rel(pill, QuickBarLayout.Label(_scale)), Left);
        using (var f = Icons(11)) using (var tb = new SolidBrush(_open ? pal.Accent1 : pal.Text2))
            g.DrawString(_open ? "" : "", f, tb, Rel(pill, QuickBarLayout.Chevron(_scale)), Center);
    }

    private void DrawSwitch(Graphics g, Palette pal, Rectangle r, bool on)
    {
        using (var path = Round(r, r.Height / 2f))
        {
            if (on) using (var b = new LinearGradientBrush(r, pal.Accent1, pal.Accent2, LinearGradientMode.Vertical)) g.FillPath(b, path);
            else using (var b = new SolidBrush(pal.Groove)) g.FillPath(b, path);
            using (var pen = new Pen(on ? A(0, 0, 0, .25) : A(0, 0, 0, .18), 1f)) g.DrawPath(pen, path);
            if (on && pal.Glow.A > 0) using (var gp = new Pen(pal.Glow, (float)(3 * _scale))) { g.SetClip(path, CombineMode.Exclude); g.DrawPath(gp, path); g.ResetClip(); }
        }
        float k = (float)(16 * _scale), pad = (r.Height - k) / 2f;
        float kx = on ? r.Right - k - pad : r.X + pad;
        using (var sb = new SolidBrush(A(0, 0, 0, .28))) g.FillEllipse(sb, kx, r.Y + pad + 1.2f * (float)_scale, k, k);
        using (var kb = new SolidBrush(pal.Knob)) g.FillEllipse(kb, kx, r.Y + pad, k, k);
    }

    private void DrawPanel(Graphics g, Palette pal, Rectangle panel)
    {
        var parts = QuickBarLayout.Panel(_scale);
        using var head = F(13, FontStyle.Bold); using var norm = F(13.5); using var small = F(12.5);
        using var tb = new SolidBrush(pal.Text); using var mb = new SolidBrush(pal.Muted);
        // how dark
        g.DrawString("How dark", head, tb, Rel(panel, parts.HowLabel), Left);
        using (var f = F(13, FontStyle.Bold)) g.DrawString(_dim + "%", f, tb, Rel(panel, parts.HowLabel), Right);
        DrawSlider(g, pal, Rel(panel, parts.Slider));
        DrawDivider(g, pal, panel, parts.Dividers[0]);
        // color
        g.DrawString("Color", head, tb, Rel(panel, parts.ColorLabel), Left);
        g.DrawString(Tints.Names[Tints.IndexOf(_tint)], small, mb, Rel(panel, parts.ColorLabel), Right);
        for (int i = 0; i < 5; i++)
        {
            var r = Rel(panel, parts.Swatches[i]);
            using (var b = new SolidBrush(Swatch[i])) g.FillEllipse(b, r);
            using (var ring = new Pen(pal.DotRing, (float)(1.5 * _scale))) g.DrawEllipse(ring, r.X + .75f, r.Y + .75f, r.Width - 1.5f, r.Height - 1.5f);
            if (i == Tints.IndexOf(_tint)) using (var sel = new Pen(pal.Accent1, (float)(2 * _scale))) { var o = (float)(3 * _scale); g.DrawEllipse(sel, r.X - o, r.Y - o, r.Width + 2 * o, r.Height + 2 * o); }
            else if (_hover == "sw" + i) using (var hv = new Pen(pal.Text2, (float)(1.2 * _scale))) { var o = (float)(3 * _scale); g.DrawEllipse(hv, r.X - o, r.Y - o, r.Width + 2 * o, r.Height + 2 * o); }
        }
        DrawDivider(g, pal, panel, parts.Dividers[1]);
        // where
        g.DrawString("Where", head, tb, Rel(panel, parts.WhereLabel), Left);
        DrawRadio(g, pal, Rel(panel, parts.RadioFollow), "Follow the window I am in", !_areaMode, _hover == "follow");
        DrawRadio(g, pal, Rel(panel, parts.RadioPick), "Only an area I pick", _areaMode, _hover == "pick");
        DrawDivider(g, pal, panel, parts.Dividers[2]);
        // pause: a plain row like Settings and Hide, with a circled pause icon
        DrawPauseRow(g, pal, Rel(panel, parts.Pause), _paused ? "Resume now" : "Pause for 1 hour", _hover == "pause");
        DrawRow(g, pal, Rel(panel, parts.Settings), "", "Settings...", _hover == "settings");
        DrawRow(g, pal, Rel(panel, parts.Hide), "", "Hide this bar", _hover == "hide");
    }

    private void DrawPauseRow(Graphics g, Palette pal, Rectangle r, string text, bool hover)
    {
        if (hover) using (var path = Round(r, 7 * (float)_scale)) using (var b = new SolidBrush(pal.Hover)) g.FillPath(b, path);
        float d = (float)(15 * _scale), cx = r.X + (float)(8 * _scale) + (float)(3.5 * _scale), cy = r.Y + r.Height / 2f;
        using (var pen = new Pen(pal.Text2, (float)(1.4 * _scale))) g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
        using (var bar = new SolidBrush(pal.Text2)) { float bw = (float)(1.8 * _scale), bh = (float)(6 * _scale), gap = (float)(2.2 * _scale); g.FillRectangle(bar, cx - gap / 2 - bw, cy - bh / 2, bw, bh); g.FillRectangle(bar, cx + gap / 2, cy - bh / 2, bw, bh); }
        using (var f = F(13.5)) using (var tb = new SolidBrush(pal.Text)) g.DrawString(text, f, tb, new Rectangle(r.X + (int)(34 * _scale), r.Y, r.Width - (int)(36 * _scale), r.Height), Left);
    }

    private void DrawRow(Graphics g, Palette pal, Rectangle r, string glyph, string text, bool hover)
    {
        if (hover) using (var path = Round(r, 7 * (float)_scale)) using (var b = new SolidBrush(pal.Hover)) g.FillPath(b, path);
        using (var f = Icons(13)) using (var tb = new SolidBrush(pal.Text2)) g.DrawString(glyph, f, tb, new Rectangle(r.X + (int)(8 * _scale), r.Y, (int)(22 * _scale), r.Height), Center);
        using (var f = F(13.5)) using (var tb = new SolidBrush(pal.Text)) g.DrawString(text, f, tb, new Rectangle(r.X + (int)(34 * _scale), r.Y, r.Width - (int)(36 * _scale), r.Height), Left);
    }

    private void DrawRadio(Graphics g, Palette pal, Rectangle r, string text, bool on, bool hover)
    {
        if (hover) using (var path = Round(r, 7 * (float)_scale)) using (var b = new SolidBrush(pal.Hover)) g.FillPath(b, path);
        float d = (float)(16 * _scale), cx = r.X + (float)(8 * _scale), cy = r.Y + r.Height / 2f;
        using (var pen = new Pen(on ? pal.Accent1 : A(pal.Text.R, pal.Text.G, pal.Text.B, .45), (float)(1.5 * _scale))) g.DrawEllipse(pen, cx, cy - d / 2, d, d);
        if (on) using (var b = new SolidBrush(pal.Accent1)) { float i = (float)(4 * _scale); g.FillEllipse(b, cx + i, cy - d / 2 + i, d - 2 * i, d - 2 * i); }
        using (var f = F(13.5)) using (var tb = new SolidBrush(pal.Text)) g.DrawString(text, f, tb, new Rectangle(r.X + (int)(34 * _scale), r.Y, r.Width - (int)(36 * _scale), r.Height), Left);
    }

    private void DrawDivider(Graphics g, Palette pal, Rectangle panel, int y)
    {
        using var pen = new Pen(pal.Divider, 1f);
        int pad = (int)(14 * _scale);
        g.DrawLine(pen, panel.X + pad, panel.Y + y, panel.Right - pad, panel.Y + y);
    }

    private void DrawSlider(Graphics g, Palette pal, Rectangle r)
    {
        float th = (float)(4 * _scale), cy = r.Y + r.Height / 2f, thumb = (float)(18 * _scale), pad = thumb / 2f;
        float x0 = r.X + pad, x1 = r.Right - pad, xv = x0 + (x1 - x0) * _dim / 100f;
        using (var tb = new SolidBrush(pal.Track)) using (var path = Round(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2)) g.FillPath(tb, path);
        if (xv > x0 + 1) using (var fb = new LinearGradientBrush(new RectangleF(x0, cy - th, xv - x0, th * 2), pal.Accent1, pal.Accent2, LinearGradientMode.Horizontal))
            using (var path = Round(new RectangleF(x0, cy - th / 2, xv - x0, th), th / 2)) g.FillPath(fb, path);
        using (var sb = new SolidBrush(A(0, 0, 0, .32))) g.FillEllipse(sb, xv - thumb / 2, cy - thumb / 2 + 1.5f * (float)_scale, thumb, thumb);
        using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, xv - thumb / 2, cy - thumb / 2, thumb, thumb);
        using (var pen = new Pen(A(0, 0, 0, .16), (float)(1 * _scale))) g.DrawEllipse(pen, xv - thumb / 2 + .5f, cy - thumb / 2 + .5f, thumb - 1f, thumb - 1f);
    }

    // ---- put the picture on screen ----

    private static void Present(IntPtr hwnd, Bitmap bmp, Box rect)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hbmp);
        try
        {
            var dst = new Native.POINT { X = rect.Left, Y = rect.Top };
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);
        }
        finally { Native.SelectObject(mem, old); Native.DeleteObject(hbmp); Native.DeleteDC(mem); Native.ReleaseDC(IntPtr.Zero, screen); }
    }

    // ---- mouse ----

    private string? HitTest(Point screenPt)
    {
        var pt = new Point(screenPt.X, screenPt.Y);
        bool In(Box b) => pt.X >= b.Left && pt.X < b.Right && pt.Y >= b.Top && pt.Y < b.Bottom;
        if (In(_pill))
        {
            Box Abs(Box part) => new(_pill.Left + part.Left, _pill.Top + part.Top, _pill.Left + part.Right, _pill.Top + part.Bottom);
            if (In(Abs(QuickBarLayout.Grip(_scale)))) return "grip";
            if (In(Abs(QuickBarLayout.Switch(_scale)))) return "switch";
            if (In(Abs(QuickBarLayout.Chevron(_scale)))) return "chevron";
            if (In(Abs(QuickBarLayout.Label(_scale)))) return "label";
            return "pill";
        }
        if (_open)
        {
            var pb = PanelBoxNow();
            if (In(pb))
            {
                var parts = QuickBarLayout.Panel(_scale);
                Box Abs(Box part) => new(pb.Left + part.Left, pb.Top + part.Top, pb.Left + part.Right, pb.Top + part.Bottom);
                var sl = Abs(parts.Slider); sl = new Box(sl.Left - 6, sl.Top - 4, sl.Right + 6, sl.Bottom + 4);
                if (In(sl)) return "slider";
                for (int i = 0; i < 5; i++) { var sw = Abs(parts.Swatches[i]); if (In(new Box(sw.Left - 4, sw.Top - 4, sw.Right + 4, sw.Bottom + 4))) return "sw" + i; }
                if (In(Abs(parts.RadioFollow))) return "follow";
                if (In(Abs(parts.RadioPick))) return "pick";
                if (In(Abs(parts.Pause))) return "pause";
                if (In(Abs(parts.Settings))) return "settings";
                if (In(Abs(parts.Hide))) return "hide";
                return "panel";
            }
        }
        return null;
    }

    private int SliderValueAt(int screenX)
    {
        var pb = PanelBoxNow(); var parts = QuickBarLayout.Panel(_scale);
        float thumb = (float)(18 * _scale), pad = thumb / 2f;
        float x0 = pb.Left + parts.Slider.Left + pad, x1 = pb.Left + parts.Slider.Right - pad;
        return Strengths.Clamp((int)Math.Round((screenX - x0) / (x1 - x0) * 100));
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_NCHITTEST:
                {
                    int x = (short)((long)m.LParam & 0xFFFF), y = (short)(((long)m.LParam >> 16) & 0xFFFF);
                    m.Result = HitTest(new Point(x, y)) is null ? (IntPtr)Native.HTTRANSPARENT : (IntPtr)1 /* HTCLIENT */;
                    return;
                }
            case Native.WM_MOUSEACTIVATE: m.Result = (IntPtr)Native.MA_NOACTIVATE; return;
            case 0x0020 /* WM_SETCURSOR */:
                {
                    var pt = Cursor.Position; var h = _dragging ? "grip" : HitTest(pt);
                    Cursor.Current = h switch { "grip" => Cursors.SizeAll, "panel" or "pill" or null => Cursors.Default, _ => Cursors.Hand };
                    m.Result = (IntPtr)1; return;
                }
            case 0x0200 /* WM_MOUSEMOVE */: OnMove(Cursor.Position); return;
            case 0x02A3 /* WM_MOUSELEAVE */: if (_hover != null) { _hover = null; Redraw(); } return;
            case 0x0215 /* WM_CAPTURECHANGED */: if (_dragging || _sliding) CancelPress(); break;
            case 0x0201 /* WM_LBUTTONDOWN */: OnDown(Cursor.Position); return;
            case 0x0202 /* WM_LBUTTONUP */: OnUp(Cursor.Position); return;
        }
        base.WndProc(ref m);
    }

    private void TrackLeave()
    {
        var t = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(), dwFlags = 2, hwndTrack = Handle };
        Native.TrackMouseEvent(ref t);
    }

    private void OnMove(Point pt)
    {
        TrackLeave();
        if (_sliding) { int v = SliderValueAt(pt.X); if (v != _dim) { _dim = v; DimPreview?.Invoke(v); Redraw(); } return; }
        if (_pressTarget is "grip" or "pill" or "label" && (_dragging || Math.Abs(pt.X - _pressAt.X) + Math.Abs(pt.Y - _pressAt.Y) > 5))
        {
            _dragging = true;
            var moved = new Box(_pressPill.Left + pt.X - _pressAt.X, _pressPill.Top + pt.Y - _pressAt.Y, _pressPill.Right + pt.X - _pressAt.X, _pressPill.Bottom + pt.Y - _pressAt.Y);
            _pill = Fit(moved, out _work, out _scale);
            Redraw(); return;
        }
        var h = HitTest(pt); if (h == "panel") h = null;
        if (h != _hover) { _hover = h; Redraw(); }
    }

    private void OnDown(Point pt)
    {
        Native.SetCapture(Handle);
        _pressAt = pt; _pressPill = _pill; _pressTarget = HitTest(pt); _dragging = false;
        if (_pressTarget == "slider") { _sliding = true; int v = SliderValueAt(pt.X); if (v != _dim) { _dim = v; DimPreview?.Invoke(v); Redraw(); } }
    }

    private void OnUp(Point pt)
    {
        bool wasSliding = _sliding, wasDragging = _dragging;
        string? target = _pressTarget; _pressTarget = null; _sliding = false; _dragging = false;
        Native.ReleaseCapture(); // (its capture-changed message finds nothing left to cancel)
        if (wasSliding) { DimCommitted?.Invoke(); return; }
        if (wasDragging)
        {
            var (work, scale, _) = ScreenOf(_pill);
            _pill = Fit(QuickBarLayout.Snap(_pill, work, (int)(18 * scale)), out _work, out _scale);
            Redraw();
            Moved?.Invoke(_pill.Left, _pill.Top);
            return;
        }
        if (target is null || HitTest(pt) != target) return; // pressed on one thing, let go on another: nothing happens
        switch (target)
        {
            case "switch": ToggleRequested?.Invoke(); break;
            case "chevron": case "label": case "pill": SetOpen(!_open); break;
            case "follow": FollowRequested?.Invoke(); break;
            case "pick": SetOpen(false); PickAreaRequested?.Invoke(); break;
            case "pause": PauseRequested?.Invoke(); break;
            case "settings": SetOpen(false); SettingsRequested?.Invoke(); break;
            case "hide": SetOpen(false); HideRequested?.Invoke(); break;
            default:
                if (target.StartsWith("sw")) TintChosen?.Invoke(Tints.Names[int.Parse(target[2..])]);
                break;
        }
    }

    /// <summary>The mouse was taken away mid-press (another window grabbed it, a dialog opened): stop dragging or sliding cleanly.</summary>
    private void CancelPress()
    {
        bool wasSliding = _sliding; _sliding = false; _dragging = false; _pressTarget = null;
        if (wasSliding) DimCommitted?.Invoke(); else Moved?.Invoke(_pill.Left, _pill.Top);
        Redraw();
    }

    private void SetOpen(bool open) { _open = open; Redraw(); }

    /// <summary>While the panel is open, a click anywhere else closes it (the bar never takes focus, so there is no deactivate event).</summary>
    private void CheckOutsideClick()
    {
        if (!_open) { _outside.Stop(); return; }
        if ((Control.MouseButtons & MouseButtons.Left) == 0) return;
        if (HitTest(Cursor.Position) is null) SetOpen(false);
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _outside.Stop(); _outside.Dispose(); _fullScreenWatch.Stop(); _fullScreenWatch.Dispose();
        foreach (var b in _shadowCache.Values) b.Dispose();
        _shadowCache.Clear();
        if (_shadow.Handle != IntPtr.Zero) _shadow.DestroyHandle();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
