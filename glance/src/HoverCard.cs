using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Glance;

/// <summary>The flyout: whole-machine meters on top, the top ten apps below, each with a two-click End button.</summary>
internal sealed class HoverCard : Form
{
    private enum RowState { Confirm, NeedsAdmin, Failed }

    public const int TopN = 10;
    private const float LogicalWidth = 340, Pad = 10, HeaderH = 60, GrowerH = 20, TabsH = 28, RowH = 32, FooterH = 30, BtnW = 60, BtnH = 20, IconPx = 16;

    /// <summary>Rows actually drawn: the card is only ever as tall as its list, never a fixed ten slots.</summary>
    private int RowCount => Math.Clamp(_rows.Count, 1, TopN);
    private float LogicalHeight => Pad + HeaderH + GrowerH + TabsH + RowCount * RowH + FooterH;

    private List<ProcessGroup> _rows = new();
    private double _memPct, _cpuPct;
    private ulong _memUsed, _memTotal;
    private int _cores = Environment.ProcessorCount;
    private string _grower = "";
    private SortMode _sort;
    private Theme _t = Theme.Current();

    private int _hoverRow = -1, _hoverTab = -1;
    private bool _hoverBtn, _hoverLink, _hoverGear;
    private string? _confirmKey;
    private RowState _confirmState;
    private DateTime _confirmUntil;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    private float _s = 1f, _fontScale;
    private Font? _fBody, _fSmall, _fBig, _fBtn, _fGlyph;
    private readonly RectangleF[] _tabs = new RectangleF[2];
    private readonly RectangleF[] _rowRects = new RectangleF[TopN];
    private readonly RectangleF[] _btns = new RectangleF[TopN];
    private RectangleF _link, _gear;
    private Rectangle _anchor;

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Pinned { get; set; }
    [System.ComponentModel.Browsable(false)]
    public SortMode Sort => _sort;

    public event Action<ProcessGroup>? KillRequested;
    public event Action<ProcessGroup>? KillAsAdminRequested;
    public event Action<ProcessGroup>? RestartExplorerRequested;
    public event Action? QuitRequested;
    public event Action<SortMode>? SortChanged;
    public event Action<Point>? GearClicked;

    public HoverCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = Instance.CardTitle;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) =>
        {
            if (_confirmKey != null && DateTime.UtcNow > _confirmUntil) { _confirmKey = null; Invalidate(); }
        };
        _timer.Start();
    }

    // Never steal focus from whatever the user is doing: it's a flyout, not a window.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int pref = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        ApplyBorder();
    }

    private void ApplyBorder()
    {
        if (!IsHandleCreated) return;
        int colorRef = _t.Separator.R | (_t.Separator.G << 8) | (_t.Separator.B << 16);
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    // Scaling can change under us (a remote session reconnecting, a monitor swap): re-measure from the logical sizes.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _fontScale = -1;
        if (Visible) ShowAt(_anchor);
    }

    public void SetSort(SortMode mode)
    {
        _sort = mode;
        Invalidate();
    }

    public void UpdateData(List<ProcessGroup> rows, SystemStats stats, string grower)
    {
        _rows = rows;
        _memPct = stats.MemPercent; _cpuPct = stats.CpuPercent;
        _memUsed = stats.MemUsed; _memTotal = stats.MemTotal; _cores = stats.Cores;
        _grower = grower;
        if (_confirmKey != null && !rows.Any(r => r.Key == _confirmKey)) _confirmKey = null;
        if (Visible && Size != TargetSize()) ShowAt(_anchor); else Invalidate();
    }

    private Size TargetSize()
    {
        float s = DeviceDpi / 96f;
        return new Size((int)(LogicalWidth * s), (int)(LogicalHeight * s));
    }

    public void MarkNeedsAdmin(string key)
    {
        _confirmKey = key;
        _confirmState = RowState.NeedsAdmin;
        _confirmUntil = DateTime.UtcNow.AddSeconds(12);
        Invalidate();
    }

    /// <summary>Windows refused even as administrator: say so on the row instead of failing silently.</summary>
    public void MarkFailed(string key)
    {
        _confirmKey = key;
        _confirmState = RowState.Failed;
        _confirmUntil = DateTime.UtcNow.AddSeconds(8);
        Invalidate();
    }

    /// <summary>Place the card next to the tray icon, inside the work area, and show it without activating it.</summary>
    public void ShowAt(Rectangle anchor)
    {
        _anchor = anchor;
        _t = Theme.Current();
        BackColor = _t.Bg;
        ApplyBorder();

        float s = DeviceDpi / 96f;
        Size = TargetSize();

        if (anchor.IsEmpty)
        {
            var w = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            anchor = new Rectangle(w.Right - 40, w.Bottom + 1, 32, 32);
        }
        var work = Screen.FromRectangle(anchor).WorkingArea;
        int gap = (int)(8 * s);

        int x = anchor.Right - Width;
        int y = anchor.Top - Height - gap;                       // taskbar at the bottom (the usual case)
        switch (Native.TaskbarEdge())
        {
            case Native.ABE_TOP: y = anchor.Bottom + gap; break;
            case Native.ABE_LEFT: x = anchor.Right + gap; y = anchor.Bottom - Height; break;
            case Native.ABE_RIGHT: x = anchor.Left - Width - gap; y = anchor.Bottom - Height; break;
        }

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - Width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - Height - gap));
        Location = new Point(x, y);

        if (!Visible) Show(); else Invalidate();
    }

    // ---- layout ----------------------------------------------------------------------------

    private void LayoutCard()
    {
        _s = DeviceDpi / 96f;
        float s = _s, w = Width;
        float y = (Pad + HeaderH + GrowerH) * s;
        _tabs[0] = new RectangleF(Pad * s, y + 4 * s, 80 * s, 20 * s);
        _tabs[1] = new RectangleF((Pad + 86) * s, y + 4 * s, 62 * s, 20 * s);
        y += TabsH * s;
        int n = RowCount;
        for (int i = 0; i < TopN; i++)
        {
            if (i >= n) { _rowRects[i] = RectangleF.Empty; _btns[i] = RectangleF.Empty; continue; }
            _rowRects[i] = new RectangleF(0, y, w, RowH * s);
            _btns[i] = new RectangleF(w - (Pad + BtnW) * s, y + (RowH - BtnH) / 2 * s, BtnW * s, BtnH * s);
            y += RowH * s;
        }
        _link = new RectangleF(Pad * s, y, 130 * s, FooterH * s);
        _gear = new RectangleF(w - (Pad + 20) * s, y + (FooterH - 20) / 2 * s, 20 * s, 20 * s);
    }

    private void EnsureFonts()
    {
        if (_fBody != null && Math.Abs(_fontScale - _s) < 0.001f) return;
        _fBody?.Dispose(); _fSmall?.Dispose(); _fBig?.Dispose(); _fBtn?.Dispose(); _fGlyph?.Dispose();
        _fBody = new Font("Segoe UI", 12 * _s, GraphicsUnit.Pixel);
        _fSmall = new Font("Segoe UI", 11 * _s, GraphicsUnit.Pixel);
        _fBig = new Font("Segoe UI", 18 * _s, FontStyle.Bold, GraphicsUnit.Pixel);
        _fBtn = new Font("Segoe UI", 11 * _s, GraphicsUnit.Pixel);
        _fGlyph = new Font("Segoe MDL2 Assets", 12 * _s, GraphicsUnit.Pixel);
        _fontScale = _s;
    }

    private static Rectangle R(RectangleF r) => Rectangle.Round(r);
    private static Rectangle R(float x, float y, float w, float h) => Rectangle.Round(new RectangleF(x, y, w, h));

    private const TextFormatFlags Base = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping;
    private const TextFormatFlags LeftMid = Base | TextFormatFlags.Left | TextFormatFlags.VerticalCenter;
    private const TextFormatFlags RightMid = Base | TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
    private const TextFormatFlags Center = Base | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

    // ---- painting --------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        LayoutCard();
        EnsureFonts();
        var g = e.Graphics;
        g.Clear(_t.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = _s, w = Width, x0 = Pad * s, y = Pad * s;
        float colW = (w - 2 * Pad * s - 16 * s) / 2;
        DrawMeter(g, new RectangleF(x0, y, colW, HeaderH * s), "Memory", $"{Fmt.Gb(_memUsed)} of {Fmt.Gb0(_memTotal)} GB", _memPct);
        DrawMeter(g, new RectangleF(x0 + colW + 16 * s, y, colW, HeaderH * s), "CPU", $"{_cores} cores", _cpuPct);
        y += HeaderH * s;

        TextRenderer.DrawText(g, _grower, _fSmall, R(x0, y, w - 2 * x0, GrowerH * s), _t.TextMuted, LeftMid | TextFormatFlags.EndEllipsis);

        DrawTab(g, _tabs[0], "By memory", _sort == SortMode.Memory, _hoverTab == 0);
        DrawTab(g, _tabs[1], "By CPU", _sort == SortMode.Cpu, _hoverTab == 1);

        for (int i = 0; i < RowCount; i++) DrawRow(g, i);

        using (var pen = new Pen(_t.Separator)) g.DrawLine(pen, 0, _link.Y, w, _link.Y);
        TextRenderer.DrawText(g, "Open Task Manager", _fBody, R(_link), _hoverLink ? _t.Text : _t.Accent, LeftMid);
        string hint = Pinned ? "Pinned · Esc or click away to close" : "Click the tile to keep this open";
        TextRenderer.DrawText(g, hint, _fSmall, R(_link.Right, _link.Y, _gear.Left - _link.Right, FooterH * s), _t.TextMuted, Center | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "", _fGlyph, R(_gear), _hoverGear ? _t.Text : _t.TextMuted, Center);
    }

    private void DrawMeter(Graphics g, RectangleF r, string label, string right, double pct)
    {
        float s = _s;
        TextRenderer.DrawText(g, label, _fBody, R(r.X, r.Y, r.Width / 2, 18 * s), _t.Text, LeftMid);
        TextRenderer.DrawText(g, right, _fSmall, R(r.X, r.Y, r.Width, 18 * s), _t.TextMuted, RightMid);
        var bar = new RectangleF(r.X, r.Y + 22 * s, r.Width, 5 * s);
        FillRounded(g, bar, 2.5f * s, _t.Track);
        float fillW = Math.Max(5 * s, bar.Width * (float)Math.Clamp(pct / 100.0, 0, 1));
        FillRounded(g, new RectangleF(bar.X, bar.Y, fillW, bar.Height), 2.5f * s, IconRenderer.BarColor(pct));
        TextRenderer.DrawText(g, $"{Math.Round(pct)}%", _fBig, R(r.X, r.Y + 30 * s, r.Width, 28 * s), _t.Text, LeftMid);
    }

    private void DrawTab(Graphics g, RectangleF r, string text, bool selected, bool hover)
    {
        if (selected) FillRounded(g, r, 6 * _s, _t.AccentBg);
        else if (hover) FillRounded(g, r, 6 * _s, _t.HoverBg);
        TextRenderer.DrawText(g, text, _fSmall, R(r), selected ? _t.Accent : hover ? _t.Text : _t.TextMuted, Center);
    }

    private void DrawRow(Graphics g, int i)
    {
        var r = _rowRects[i];
        float s = _s;
        using (var pen = new Pen(_t.Separator)) g.DrawLine(pen, 0, r.Y, r.Right, r.Y);
        if (i >= _rows.Count) return;

        var p = _rows[i];
        bool failed = _confirmKey == p.Key && _confirmState == RowState.Failed;
        bool confirm = _confirmKey == p.Key && !failed;
        if (confirm) { using var b = new SolidBrush(_t.DangerBg); g.FillRectangle(b, r.X, r.Y + 1, r.Width, r.Height - 1); }
        else if (_hoverRow == i) { using var b = new SolidBrush(_t.HoverBg); g.FillRectangle(b, r.X, r.Y + 1, r.Width, r.Height - 1); }

        var ir = new RectangleF(Pad * s, r.Y + (RowH - IconPx) / 2 * s, IconPx * s, IconPx * s);
        if (p.Icon != null) g.DrawImage(p.Icon, ir);
        else
        {
            FillRounded(g, ir, 4 * s, _t.Track);
            TextRenderer.DrawText(g, p.Initials, _fSmall, R(ir), _t.TextMuted, Center);
        }

        var btn = _btns[i];
        float cpuW = 36 * s, memW = 58 * s;
        var cpuR = new RectangleF(btn.X - 8 * s - cpuW, r.Y, cpuW, r.Height);
        var memR = new RectangleF(cpuR.X - 6 * s - memW, r.Y, memW, r.Height);
        float nx = ir.Right + 8 * s;
        var nameR = new RectangleF(nx, r.Y + 1 * s, memR.X - 8 * s - nx, 16 * s);
        var subR = new RectangleF(nx, r.Y + 16 * s, nameR.Width, 15 * s);

        Color nameColor = p.Protected ? _t.TextMuted : _t.Text;
        TextRenderer.DrawText(g, p.Name, _fBody, R(nameR), nameColor, LeftMid | TextFormatFlags.EndEllipsis);

        string sub = p.Pids.Count == 1 ? "1 process" : $"{p.Pids.Count} processes";
        if (p.Protected) sub += " · Windows, protected";
        else if (failed) sub += " · Windows wouldn't end it";
        else if (confirm && _confirmState == RowState.NeedsAdmin) sub += " · Windows refused, try as admin";
        else if (confirm) sub += p.IsExplorer ? " · click again to restart" : " · click again to end";
        TextRenderer.DrawText(g, sub, _fSmall, R(subR), confirm || failed ? _t.Danger : _t.TextMuted, LeftMid | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, Fmt.Bytes(p.Memory), _fBody, R(memR), nameColor, RightMid);
        TextRenderer.DrawText(g, $"{Math.Round(p.Cpu)}%", _fBody, R(cpuR), _t.TextMuted, RightMid);

        if (p.Protected)
        {
            TextRenderer.DrawText(g, "", _fGlyph, R(btn), _t.TextMuted, Center);
            return;
        }

        string label = p.IsSelf ? "Quit"
            : p.IsExplorer ? (confirm ? "Restart?" : "Restart")
            : confirm ? (_confirmState == RowState.NeedsAdmin ? "As admin" : "End?") : "End";
        bool hot = _hoverRow == i && _hoverBtn;
        Color border = confirm ? _t.Danger : _t.ButtonBorder;
        Color text = confirm ? _t.Danger : _t.Text;
        if (confirm) FillRounded(g, btn, 6 * s, _t.Bg);
        else if (hot) FillRounded(g, btn, 6 * s, _t.HoverBg);
        using (var path = IconRenderer.Rounded(btn, 6 * s))
        using (var pen = new Pen(border)) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, label, _fBtn, R(btn), text, Center);
    }

    private static void FillRounded(Graphics g, RectangleF r, float radius, Color color)
    {
        using var path = IconRenderer.Rounded(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    // ---- mouse -----------------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        LayoutCard();
        var p = e.Location;
        int row = -1, tab = -1;
        bool btn = false;
        for (int i = 0; i < TopN; i++)
        {
            if (!_rowRects[i].Contains(p)) continue;
            row = i;
            btn = _btns[i].Contains(p) && i < _rows.Count && !_rows[i].Protected;
        }
        for (int t = 0; t < 2; t++) if (_tabs[t].Contains(p)) tab = t;
        bool link = _link.Contains(p), gear = _gear.Contains(p);

        if (row != _hoverRow || btn != _hoverBtn || tab != _hoverTab || link != _hoverLink || gear != _hoverGear)
        {
            _hoverRow = row; _hoverBtn = btn; _hoverTab = tab; _hoverLink = link; _hoverGear = gear;
            Invalidate();
        }
        Cursor = (btn || tab >= 0 || link || gear) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverRow = -1; _hoverTab = -1; _hoverBtn = _hoverLink = _hoverGear = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        LayoutCard();
        var p = e.Location;

        for (int t = 0; t < 2; t++)
        {
            if (!_tabs[t].Contains(p)) continue;
            var mode = (SortMode)t;
            if (mode != _sort) { _sort = mode; Invalidate(); SortChanged?.Invoke(mode); }
            return;
        }
        if (_link.Contains(p))
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose(); } catch { }
            return;
        }
        if (_gear.Contains(p))
        {
            GearClicked?.Invoke(PointToScreen(new Point((int)_gear.Left, (int)_gear.Bottom)));
            return;
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_btns[i].Contains(p)) { HandleButton(_rows[i]); return; }
        }
    }

    /// <summary>Plain-text state for the --preview smoke test: one line per row with the End button's screen rectangle.</summary>
    public string DebugRows()
    {
        LayoutCard();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _rows.Count; i++)
        {
            var p = _rows[i];
            string flags = (p.Protected ? "protected " : "") + (p.IsExplorer ? "explorer " : "") + (p.IsSelf ? "self " : "");
            sb.AppendLine($"{i}|{p.Name}|{p.Key}|{string.Join(',', p.Pids)}|{p.Memory}|{flags.Trim()}|btn={RectangleToScreen(R(_btns[i]))}");
        }
        return sb.ToString();
    }

    private void HandleButton(ProcessGroup g)
    {
        if (g.Protected) return;
        if (g.IsSelf) { QuitRequested?.Invoke(); return; }

        if (_confirmKey != g.Key || _confirmState == RowState.Failed)
        {
            // First click only arms the button. Nothing happens on a stray click.
            _confirmKey = g.Key;
            _confirmState = RowState.Confirm;
            _confirmUntil = DateTime.UtcNow.AddSeconds(3.5);
            Invalidate();
            return;
        }

        var state = _confirmState;
        _confirmKey = null;
        Invalidate();
        if (state == RowState.NeedsAdmin) { KillAsAdminRequested?.Invoke(g); return; }
        if (g.IsExplorer) RestartExplorerRequested?.Invoke(g);
        else KillRequested?.Invoke(g);
    }
}
