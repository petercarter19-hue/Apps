using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoTwins;

/// <summary>
/// The app's own pictures of itself. Draws the screen into a picture file without ever showing a window,
/// so nothing is captured from the real screen and no mouse or keyboard is touched.
/// </summary>
public static class SelfCheck
{
    /// <summary>Draws <paramref name="view"/> at the given Windows scaling (1.25 = 125%) and saves a PNG. Returns the picture size in pixels.</summary>
    public static (int w, int h) RenderToPng(FrameworkElement view, double widthDip, double heightDip, double scale, string pngPath, Brush? backdrop = null)
    {
        var host = Host(view, widthDip, heightDip, scale, backdrop);
        int pw = (int)Math.Ceiling(widthDip * scale), ph = (int)Math.Ceiling(heightDip * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
        using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
        return (pw, ph);
    }

    /// <summary>Lays the view out as it would be at this scaling, off screen.</summary>
    /// <param name="backdrop">What is behind the window (a wallpaper, drawn already blurred as Windows' glass would). Default: the plain page color.</param>
    public static FrameworkElement Host(FrameworkElement view, double widthDip, double heightDip, double scale, Brush? backdrop = null)
    {
        Glass.Refresh();
        var bg = backdrop
                 ?? Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush
                 ?? Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush
                 ?? SystemColors.WindowBrush;
        var host = new Grid { Width = widthDip, Height = heightDip, Background = bg };
        // the fog a real window has as its background (a window itself isn't needed, so nothing is ever shown on the real screen)
        var fog = new Border { Background = Application.Current.TryFindResource("GlassWindowBrush") as Brush };
        fog.Child = view;
        host.Children.Add(fog);
        VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
        host.Measure(new Size(widthDip, heightDip));
        host.Arrange(new Rect(0, 0, widthDip, heightDip));
        host.UpdateLayout();
        return host;
    }

    /// <summary>Finds any text on screen that doesn't fit its box (cut off or trimmed). An empty list means nothing is cut off.</summary>
    public static List<string> FindCutOffText(DependencyObject root)
    {
        var bad = new List<string>();
        Walk(root, bad);
        return bad;
    }

    private static void Walk(DependencyObject d, List<string> bad)
    {
        if (d is TextBlock tb && Showing(tb) && !string.IsNullOrEmpty(tb.Text) && tb.ActualWidth > 0)
        {
            // WPF gives text the room it really needs and then clips the excess: a clip smaller than the text is cut-off text.
            var clip = LayoutInformation.GetLayoutClip(tb);
            bool clipped = clip != null && (clip.Bounds.Width + 0.75 < tb.RenderSize.Width || clip.Bounds.Height + 0.75 < tb.RenderSize.Height);
            // "..." trimming hides text too.
            bool trimmed = false;
            if (tb.TextTrimming != TextTrimming.None)
            {
                var probe = new TextBlock { Text = tb.Text, FontFamily = tb.FontFamily, FontSize = tb.FontSize, FontWeight = tb.FontWeight, FontStyle = tb.FontStyle };
                VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(tb));
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                trimmed = probe.DesiredSize.Width > tb.ActualWidth + 0.75;
            }
            if (clipped || trimmed) bad.Add($"\"{Shorten(tb.Text)}\" ({tb.RenderSize.Width:0}x{tb.RenderSize.Height:0}, shown {(clip?.Bounds.Width ?? tb.RenderSize.Width):0}x{(clip?.Bounds.Height ?? tb.RenderSize.Height):0})");
        }
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), bad);
    }

    /// <summary>Finds text that would be hard to read: less than 4.5 to 1 contrast against what is behind it (turned-off buttons are allowed to be faint).</summary>
    public static List<string> FindLowContrastText(DependencyObject root, Color pageBackground)
    {
        var bad = new List<string>();
        void Visit(DependencyObject d)
        {
            // (scroll-bar arrows and other icon glyphs are drawings, not words, so they are not judged as text)
            if (d is TextBlock tb && Showing(tb) && tb.IsEnabled && !string.IsNullOrEmpty(tb.Text) && !tb.Text.All(c => c >= '' && c <= '')
                && tb.Foreground is SolidColorBrush fg)
            {
                // every background behind the text, nearest first; a gradient counts as each color it has behind the text's own lines
                var layers = new List<List<Color>>();
                for (DependencyObject? p = VisualTreeHelper.GetParent(tb); p != null; p = VisualTreeHelper.GetParent(p))
                {
                    var brush = p is Border b ? b.Background : p is Panel pn ? pn.Background : null;
                    if (brush != null && p is FrameworkElement owner && ColorsBehind(brush, owner, tb) is { Count: > 0 } colors) layers.Add(colors);
                }
                layers.Reverse();
                // the worst mix of them (each layer has only a few colors, so trying every mix is quick)
                var backs = new List<Color> { pageBackground };
                foreach (var l in layers)
                {
                    backs = backs.SelectMany(under => l.Select(top => Blend(top, under))).Distinct().ToList();
                    if (backs.Count > 64) backs = new List<Color> { backs.MinBy(Luminance), backs.MaxBy(Luminance) };
                }
                double ratio = backs.Min(back => Contrast(Blend(fg.Color, back), back));
                if (ratio < 4.5) bad.Add($"\"{Shorten(tb.Text)}\" contrast {ratio:0.0}:1");
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Visit(VisualTreeHelper.GetChild(d, i));
        }
        Visit(root);
        return bad;
    }

    /// <summary>
    /// The colors of a background that are behind a piece of text. A plain color is itself. A top-to-bottom gradient is the colors it has
    /// between the top and the bottom of the text (so a glossy button is judged where its words are). Any other gradient counts as all its colors.
    /// See-through colors are skipped (nothing to add).
    /// </summary>
    private static List<Color> ColorsBehind(Brush brush, FrameworkElement owner, FrameworkElement text)
    {
        var colors = new List<Color>();
        if (brush is SolidColorBrush s) colors.Add(s.Color);
        else if (brush is LinearGradientBrush lg && lg.GradientStops.Count > 0 && Math.Abs(lg.StartPoint.X - lg.EndPoint.X) < 1e-9 && lg.EndPoint.Y != lg.StartPoint.Y
                 && owner.ActualHeight > 0 && text.IsDescendantOf(owner))
        {
            var r = text.TransformToAncestor(owner).TransformBounds(new Rect(text.RenderSize));
            double Along(double y) => lg.MappingMode == BrushMappingMode.RelativeToBoundingBox
                ? (y / owner.ActualHeight - lg.StartPoint.Y) / (lg.EndPoint.Y - lg.StartPoint.Y)
                : (y - lg.StartPoint.Y) / (lg.EndPoint.Y - lg.StartPoint.Y);
            double t0 = Math.Clamp(Math.Min(Along(r.Top), Along(r.Bottom)), 0, 1), t1 = Math.Clamp(Math.Max(Along(r.Top), Along(r.Bottom)), 0, 1);
            colors.Add(At(lg, t0));
            colors.Add(At(lg, t1));
            colors.AddRange(lg.GradientStops.Where(g => g.Offset > t0 && g.Offset < t1).Select(g => g.Color));
        }
        else if (brush is GradientBrush g) colors.AddRange(g.GradientStops.Select(x => x.Color));
        return colors.Where(c => c.A > 0).Select(c => Color.FromArgb((byte)Math.Round(c.A * brush.Opacity), c.R, c.G, c.B)).Distinct().ToList();
    }

    /// <summary>The color of a gradient at a point along it (0 = start, 1 = end).</summary>
    private static Color At(GradientBrush g, double t)
    {
        var stops = g.GradientStops.OrderBy(s => s.Offset).ToList();
        if (t <= stops[0].Offset) return stops[0].Color;
        if (t >= stops[^1].Offset) return stops[^1].Color;
        for (int i = 1; i < stops.Count; i++)
            if (t <= stops[i].Offset)
            {
                var (a, b) = (stops[i - 1], stops[i]);
                double f = b.Offset == a.Offset ? 1 : (t - a.Offset) / (b.Offset - a.Offset);
                byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * f);
                return Color.FromArgb(Mix(a.Color.A, b.Color.A), Mix(a.Color.R, b.Color.R), Mix(a.Color.G, b.Color.G), Mix(a.Color.B, b.Color.B));
            }
        return stops[^1].Color;
    }

    private static double Luminance(Color c)
    {
        double Ch(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static Color Blend(Color top, Color under)
    {
        double a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static double Contrast(Color a, Color b)
    {
        double L(Color c)
        {
            double Ch(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        double la = L(a), lb = L(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The colour of the page behind the screen, for the contrast check.</summary>
    public static Color PageBackground() =>
        (Application.Current.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush)?.Color ?? Colors.White;

    /// <summary>Visible on screen: itself and everything above it is switched on (an off-screen picture has no window to ask).</summary>
    private static bool Showing(UIElement e)
    {
        DependencyObject? d = e;
        while (d != null)
        {
            if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return true;
    }

    private static string Shorten(string s) => s.Length <= 50 ? s : s[..50] + "…";
}
