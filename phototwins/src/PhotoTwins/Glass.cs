using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PhotoTwins;


/// <summary>
/// The glossy look (Pete 10-29: "a black to white transition fade with a glass cards overlay", rounder buttons with texture, depth and
/// shadows). The main window has a black band at the top (name, buttons, headline) that fades down to white (to soft graphite in dark mode);
/// the groups sit on clear glass cards over the fade (Pete 10-30: "The foreground should be glass"): mostly see-through, so the fade
/// shows through them, with a bright rim and a soft shine across the top. Buttons are rounded pills with a glossy face, a fine grain and a soft shadow. Windows
/// are solid (nothing of the wallpaper shows), so the look is the same on every wallpaper and every Windows. With high contrast on, everything
/// falls back to Windows' own plain colors and controls. Photos are never covered by anything. The styles live in Theme.xaml; the colors,
/// which depend on light or dark, are set here.
/// </summary>
public static class Glass
{
    // ---- the black band at the top of the main window (the same in light and dark)
    public static readonly Color HeroTop = Color.FromRgb(0x05, 0x05, 0x07);


    // ---- dialogs: the same idea, gentler (a soft gray top fading to white, or near-black to graphite)
    public static readonly Color LightDialogTop = Color.FromRgb(0xE3, 0xE5, 0xEA);
    public static readonly Color DarkDialogTop = Color.FromRgb(0x14, 0x14, 0x17);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35, DwmwaTextColor = 36;   // Windows 11: the title bar's own colors
    private const uint DwmColorDefault = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    /// <summary>Tests can force the look on or off; null = on unless Windows' high contrast is on.</summary>
    public static bool? Forced { get; set; }

    /// <summary>The rule: the glossy look everywhere, except with high contrast on, where Windows' own plain colors are used.</summary>
    public static bool SupportedFor(bool highContrast) => !highContrast;

    public static bool IsOn => Forced ?? SupportedFor(SystemParameters.HighContrast);

    /// <summary>Light or dark, read from the theme the app is showing right now.</summary>
    public static bool IsDark
    {
        get
        {
            var b = Application.Current?.TryFindResource("SolidBackgroundFillColorBaseBrush") as SolidColorBrush;
            if (b == null) return false;
            var c = b.Color;
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
        }
    }

    /// <summary>The look's styles that stay with high contrast (they take their colors from Windows then).</summary>
    private static readonly HashSet<object> KeptWhenPlain = new() { "GlassCard", "LinkButton", "HeroLink", "PillFocus", "PillButtonTemplate" };

    private static string? _applied;
    private static ResourceDictionary? _theme;
    private static readonly List<(WeakReference<Window> w, bool hero)> Windows = new();
    private static bool _watching;

    /// <summary>The result of the last call that set a title bar's colors (0 = accepted). For the start-up self-check.</summary>
    public static int LastFrameResult { get; private set; } = -1;

    /// <summary>The styles of the look (Theme.xaml), loaded once.</summary>
    public static ResourceDictionary Theme => _theme ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/PhotoTwins;component/Theme.xaml") };

    /// <summary>
    /// Puts the look's styles and colors in place for light or dark (or takes them away for high contrast). Safe to call again at any time;
    /// does nothing if nothing changed.
    /// </summary>
    public static void Refresh(bool force = false)
    {
        var app = Application.Current;
        if (app == null) return;
        bool dark = IsDark, on = IsOn;
        var state = $"{(dark ? "dark" : "light")}-{(on ? "gloss" : "plain")}";
        if (!force && state == _applied && app.Resources.Contains("GlassWindowBrush")) return;
        _applied = state;

        // the styles go straight into the app's own resources, so they win over Windows' built-in ones whatever order those are loaded in.
        // With high contrast the buttons and tabs are Windows' own (they follow the high-contrast colors); the cards and links stay, drawn in
        // Windows' plain colors (set below).
        var r = app.Resources;
        foreach (var key in Theme.Keys.Cast<object>().ToList())
        {
            if (on || KeptWhenPlain.Contains(key)) r[key] = Theme[key];
            else r.Remove(key);
        }
        if (!on)
        {
            // a screen that asks for one of the look's button or tab styles gets Windows' own instead (never the bare old-Windows style)
            r["HeroButton"] = app.TryFindResource(typeof(Button));
            r["PillTabControl"] = app.TryFindResource(typeof(TabControl));
            r["PillTab"] = app.TryFindResource(typeof(TabItem));
            r["HeroAccentButton"] = app.TryFindResource("AccentButtonStyle");
        }

        if (on)
        {
            foreach (var (key, brush) in Brushes(dark)) r[key] = brush;
            r["GrainBrush"] = Grain;
            // secondary text a little stronger than Windows' own, so it stays easy to read on the glass
            r["TextFillColorSecondaryBrush"] = Solid(dark ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD9, 0x00, 0x00, 0x00));
        }
        else
        {
            r.Remove("TextFillColorSecondaryBrush");   // back to Windows' own
            Brush Sys(string key, Brush fallback) => (app.TryFindResource(key) as Brush) ?? fallback;
            var window = Sys("SolidBackgroundFillColorBaseBrush", SystemColors.WindowBrush);
            var text = Sys("TextFillColorPrimaryBrush", SystemColors.WindowTextBrush);
            var text2 = Sys("TextFillColorSecondaryBrush", SystemColors.WindowTextBrush);
            var edge = Sys("CardStrokeColorDefaultBrush", SystemColors.WindowTextBrush);
            foreach (var key in new[] { "GlassWindowBrush", "HeroBrush", "PageBrush", "GlassCardBrush", "GlassPhotoCardBrush", "ChipBrush", "HeroChipBrush", "TabTrackBrush", "PictureWellBrush" })
                r[key] = window;
            r["GlassEdgeBrush"] = edge;
            r["HeroTextBrush"] = text; r["HeroSecondaryTextBrush"] = text2;
            r["ChipTextBrush"] = Sys("AccentTextFillColorPrimaryBrush", SystemColors.HotTrackBrush);
            r["HeroChipTextBrush"] = r["ChipTextBrush"];
            r["HeroSheenBrush"] = System.Windows.Media.Brushes.Transparent;
            r["GlassSheenBrush"] = System.Windows.Media.Brushes.Transparent;
            r["GrainBrush"] = System.Windows.Media.Brushes.Transparent;
            r["KeeperBrush"] = Sys("AccentFillColorDefaultBrush", SystemColors.HighlightBrush);
            r["KeeperTextBrush"] = Sys("TextOnAccentFillColorPrimaryBrush", SystemColors.HighlightTextBrush);
            r["ShadowBrush"] = System.Windows.Media.Brushes.Transparent;
            r["FocusRingBrush"] = SystemColors.HighlightBrush;
        }

        foreach (var (wr, hero) in Windows.ToList())
        {
            if (wr.TryGetTarget(out var w)) { try { Frame(w, hero); } catch { /* the window is closing */ } }
            else Windows.Remove((wr, hero));
        }
    }

    /// <summary>Makes a window part of the look. Call once, after InitializeComponent. The main window passes hero = true (black title bar).</summary>
    public static void Apply(Window w, bool hero = false)
    {
        Refresh();
        w.SetResourceReference(Control.BackgroundProperty, hero ? "PageBrush" : "GlassWindowBrush");
        lock (Windows) Windows.Add((new WeakReference<Window>(w), hero));
        w.SourceInitialized += (_, _) => { try { Frame(w, hero); } catch { /* Windows' own title bar */ } };
        w.Activated += (_, _) => Refresh();   // picks up a change of Windows theme made while the app was open
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => { Refresh(); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// Colors the window's title bar to run into the page: black for the main window (so it joins the black band), the dialog's top color for
    /// dialogs; Windows' own title bar with high contrast. Returns Windows' answer (0 = accepted).
    /// </summary>
    public static int Frame(Window w, bool hero)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return -1;
        bool on = IsOn, darkPage = IsDark;
        int darkBar = on && (hero || darkPage) ? 1 : (darkPage ? 1 : 0);
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkBar, sizeof(int));
        uint caption = DwmColorDefault, text = DwmColorDefault;
        if (on)
        {
            var c = hero ? HeroTop : darkPage ? DarkDialogTop : LightDialogTop;
            caption = ColorRef(c);
            text = darkBar == 1 ? ColorRef(Colors.White) : ColorRef(Color.FromRgb(0x1B, 0x1C, 0x20));
        }
        int hr = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(uint));
        if (on) LastFrameResult = hr;
        return hr;
    }

    private static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    // ---- the colors

    private static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>A top-to-bottom gradient. Stops are (offset, color).</summary>
    private static LinearGradientBrush Down(params (double at, string color)[] stops) => Down(false, 0, stops);

    private static LinearGradientBrush Down(bool absolute, double height, params (double at, string color)[] stops)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = absolute ? new Point(0, height) : new Point(0, 1),
            MappingMode = absolute ? BrushMappingMode.Absolute : BrushMappingMode.RelativeToBoundingBox,
        };
        foreach (var (at, color) in stops) b.GradientStops.Add(new GradientStop(C(color), at));
        b.Freeze();
        return b;
    }

    /// <summary>Every color of the look for light or dark, by resource name.</summary>
    public static IEnumerable<(string key, Brush brush)> Brushes(bool dark)
    {
        string hex(Color c) => c.ToString();
        // same in light and dark
        yield return ("HeroBrush", Down((0, "#F0050507"), (1, "#E0141418")));                 // dark glass: the background shows faintly through it
        yield return ("PageBrush", dark ? Down((0, "#050506"), (0.4, "#17181B"), (1, "#2C2D31"))
                                        : Down((0, "#1E1F23"), (0.08, "#45464A"), (0.16, "#C4C5C9"), (0.3, "#EDEEF0"), (1, "#FAFAFB")));
        // (light: the fade is done by the top of the first card, so the clear glass cards always sit on near-white and their text stays sharp)
        yield return ("HeroSheenBrush", Sheen());
        yield return ("HeroTextBrush", Solid(Colors.White));
        yield return ("HeroSecondaryTextBrush", Solid(C("#BABBC0")));
        yield return ("HeroChipBrush", Solid(C("#1C2333")));
        yield return ("HeroChipTextBrush", Solid(C("#9CC2FF")));
        yield return ("HeroButtonFaceBrush", Down((0, "#414247"), (0.5, "#2B2C31"), (1, "#202126")));
        yield return ("HeroButtonHoverBrush", Down((0, "#4E4E54"), (0.5, "#35363B"), (1, "#26272C")));
        yield return ("HeroButtonPressedBrush", Down((0, "#18191D"), (1, "#26272C")));
        yield return ("HeroButtonEdgeBrush", Down((0, "#6C6C72"), (1, "#2A2B30")));
        yield return ("HeroDisabledFaceBrush", Solid(C("#1D1E22")));
        yield return ("HeroDisabledTextBrush", Solid(C("#6C6E72")));
        yield return ("AccentFaceBrush", Down((0, "#5B9BFF"), (0.1, "#2E6FE8"), (0.5, "#2260D8"), (1, "#1849B8")));
        yield return ("AccentHoverBrush", Down((0, "#6AA6FF"), (0.1, "#3576EC"), (0.5, "#2866DD"), (1, "#1B50C2")));
        yield return ("AccentPressedBrush", Down((0, "#16409E"), (1, "#2260D8")));
        yield return ("AccentEdgeBrush", Down((0, "#8DBBFF"), (0.5, "#2A63D0"), (1, "#123A94")));
        yield return ("AccentTextBrush", Solid(Colors.White));
        yield return ("KeeperBrush", Down((0, "#F7E2A6"), (0.5, "#E3C072"), (1, "#C99F48")));
        yield return ("KeeperTextBrush", Solid(C("#1A1405")));
        yield return ("FocusRingBrush", Solid(C("#4C8DFF")));

        if (!dark)
        {
            yield return ("GlassWindowBrush", Down((0, hex(LightDialogTop)), (1, "#FAFAFB")));
            yield return ("GlassCardBrush", Down((0, "#26FFFFFF"), (1, "#14FFFFFF")));          // clear glass (Pete 10-30: "make it glass. So it's see-through")
            yield return ("GlassPhotoCardBrush", Down((0, "#40FFFFFF"), (1, "#2EFFFFFF")));     // a second, barely frostier layer for each photo
            yield return ("GlassEdgeBrush", Down((0, "#FFFFFFFF"), (0.5, "#33000000"), (1, "#40000000")));
            yield return ("GlassSheenBrush", Down((0, "#73FFFFFF"), (0.35, "#00FFFFFF")));
            yield return ("ButtonFaceBrush", Down((0, "#FFFFFF"), (0.5, "#F6F7F9"), (1, "#E4E6EA")));
            yield return ("ButtonHoverBrush", Down((0, "#FFFFFF"), (1, "#EEF0F4")));
            yield return ("ButtonPressedBrush", Down((0, "#DDDFE3"), (1, "#EBEDF1")));
            yield return ("ButtonEdgeBrush", Down((0, "#D7D8DC"), (1, "#ABACB1")));
            yield return ("ButtonTextBrush", Solid(C("#1B1C20")));
            yield return ("DisabledFaceBrush", Solid(C("#ECEDF0")));
            yield return ("DisabledEdgeBrush", Solid(C("#D9DBDF")));
            yield return ("DisabledTextBrush", Solid(C("#9B9DA1")));
            yield return ("TabTrackBrush", Down((0, "#F0D5D7DB"), (1, "#F0E2E4E8")));
            yield return ("TabSelectedBrush", Down((0, "#FFFFFF"), (1, "#EFF0F3")));
            yield return ("TabHoverBrush", Solid(C("#66FFFFFF")));
            yield return ("TabTextBrush", Solid(C("#3E4044")));
            yield return ("TabSelectedTextBrush", Solid(C("#111216")));
            yield return ("ChipBrush", Solid(C("#E7EFFD")));
            yield return ("ChipHoverBrush", Solid(C("#D6E4FC")));
            yield return ("ChipTextBrush", Solid(C("#1450C8")));
            yield return ("PictureWellBrush", Solid(C("#1A000000")));
            yield return ("ShadowBrush", Solid(C("#000000")));
        }
        else
        {
            yield return ("GlassWindowBrush", Down((0, hex(DarkDialogTop)), (1, "#232428")));
            yield return ("GlassCardBrush", Down((0, "#1CFFFFFF"), (1, "#10FFFFFF")));
            yield return ("GlassPhotoCardBrush", Down((0, "#1CFFFFFF"), (1, "#12FFFFFF")));
            yield return ("GlassEdgeBrush", Down((0, "#66FFFFFF"), (1, "#0DFFFFFF")));
            yield return ("GlassSheenBrush", Down((0, "#24FFFFFF"), (0.35, "#00FFFFFF")));
            yield return ("ButtonFaceBrush", Down((0, "#5B5C61"), (0.5, "#44444A"), (1, "#34353A")));
            yield return ("ButtonHoverBrush", Down((0, "#67686D"), (0.5, "#4C4D52"), (1, "#3B3C41")));
            yield return ("ButtonPressedBrush", Down((0, "#2B2C31"), (1, "#3A3B40")));
            yield return ("ButtonEdgeBrush", Down((0, "#7B7C81"), (1, "#26272C")));
            yield return ("ButtonTextBrush", Solid(Colors.White));
            yield return ("DisabledFaceBrush", Solid(C("#2D2E33")));
            yield return ("DisabledEdgeBrush", Solid(C("#3A3B40")));
            yield return ("DisabledTextBrush", Solid(C("#76787C")));
            yield return ("TabTrackBrush", Down((0, "#F01A1B1F"), (1, "#F024252A")));
            yield return ("TabSelectedBrush", Down((0, "#56575C"), (1, "#3C3C42")));
            yield return ("TabHoverBrush", Solid(C("#1FFFFFFF")));
            yield return ("TabTextBrush", Solid(C("#C7C9CD")));
            yield return ("TabSelectedTextBrush", Solid(Colors.White));
            yield return ("ChipBrush", Solid(C("#26314A")));
            yield return ("ChipHoverBrush", Solid(C("#2F3D5C")));
            yield return ("ChipTextBrush", Solid(C("#9CC2FF")));
            yield return ("PictureWellBrush", Solid(C("#33000000")));
            yield return ("ShadowBrush", Solid(C("#000000")));
        }
    }

    /// <summary>The soft gloss across the top of the black band: a faint white glow from the top center.</summary>
    private static Brush Sheen()
    {
        var b = new RadialGradientBrush { Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.75, RadiusY = 0.9 };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        b.Freeze();
        return b;
    }

    private static ImageBrush? _grain;

    /// <summary>The fine texture on the buttons: tiny specks of white and black, at most 4% strong, the same every time.</summary>
    public static ImageBrush Grain => _grain ??= MakeGrain();

    private static ImageBrush MakeGrain()
    {
        const int n = 128;
        var px = new byte[n * n * 4];
        var rnd = new Random(1029);
        for (int i = 0; i < n * n; i++)
        {
            byte a = (byte)rnd.Next(0, 11);
            byte v = rnd.Next(2) == 0 ? (byte)0 : (byte)(a);   // premultiplied: white speck = (a,a,a,a), black speck = (0,0,0,a)
            px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = a;
        }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        var b = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, n, n), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        b.Freeze();
        return b;
    }
}
