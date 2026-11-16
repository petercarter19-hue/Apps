namespace Spotlight;

/// <summary>
/// Sizes and places of the quick bar (the small floating pill with its drop-down panel). All numbers are for 100% display scale and are
/// multiplied by the scale of the screen the pill is on. Plain math, no Windows calls, so it is tested with made-up screens.
/// </summary>
internal static class QuickBarLayout
{
    // ---- pill (the resting bar) ----
    public const int PillW = 151, PillH = 40;
    public const int PanelW = 256;
    public const int ShadowSide = 60, ShadowTop = 50, ShadowBottom = 80;

    public static int S(int v, double scale) => (int)Math.Round(v * scale);

    public static Box PillRect(int left, int top, double scale) => new(left, top, left + S(PillW, scale), top + S(PillH, scale));

    /// <summary>Parts of the pill, relative to the pill's top-left corner.</summary>
    public static Box Grip(double s) => new(S(6, s), S(6, s), S(26, s), S(PillH - 6, s));
    public static Box Switch(double s) => new(S(30, s), S(10, s), S(64, s), S(30, s));
    public static Box Label(double s) => new(S(72, s), S(8, s), S(122, s), S(32, s));
    public static Box Chevron(double s) => new(S(122, s), S(4, s), S(PillW - 4, s), S(PillH - 4, s));

    // ---- panel rows, relative to the panel's top-left corner ----
    public sealed record PanelParts(
        int Width, int Height, Box HowLabel, Box Slider, Box ColorLabel, IReadOnlyList<Box> Swatches, Box WhereLabel,
        Box RadioFollow, Box RadioPick, Box Pause, Box Settings, Box Hide, IReadOnlyList<int> Dividers);

    public static PanelParts Panel(double s)
    {
        int w = S(PanelW, s), pad = S(16, s);
        int y = S(14, s);
        var how = new Box(pad, y, w - pad, y + S(20, s)); y += S(22, s);
        var slider = new Box(pad, y, w - pad, y + S(26, s)); y += S(26, s) + S(8, s);
        int d1 = y; y += S(10, s);
        var colorLabel = new Box(pad, y, w - pad, y + S(20, s)); y += S(24, s);
        var swatches = new List<Box>();
        int sw = S(22, s), gap = S(10, s);
        for (int i = 0; i < 5; i++) { int x = pad + i * (sw + gap); swatches.Add(new Box(x, y, x + sw, y + sw)); }
        y += sw + S(10, s);
        int d2 = y; y += S(10, s);
        var whereLabel = new Box(pad, y, w - pad, y + S(20, s)); y += S(22, s);
        var r1 = new Box(pad, y, w - pad, y + S(28, s)); y += S(28, s);
        var r2 = new Box(pad, y, w - pad, y + S(28, s)); y += S(28, s) + S(8, s);
        int d3 = y; y += S(10, s);
        var pause = new Box(pad, y, w - pad, y + S(30, s)); y += S(30, s);
        var settings = new Box(pad, y, w - pad, y + S(30, s)); y += S(30, s);
        var hide = new Box(pad, y, w - pad, y + S(30, s)); y += S(30, s);
        int h = y + S(12, s);
        return new PanelParts(w, h, how, slider, colorLabel, swatches, whereLabel, r1, r2, pause, settings, hide, new[] { d1, d2, d3 });
    }

    // ---- where things go on the screen ----

    /// <summary>The first-run spot: top center of the work area (like the Windows screenshot bar), so the panel drops down beneath it.</summary>
    public static Box DefaultPill(Box work, double s)
    {
        int w = S(PillW, s), h = S(PillH, s);
        int left = work.Left + (work.Width - w) / 2, top = work.Top + S(24, s);
        return new Box(left, top, left + w, top + h);
    }

    /// <summary>Keeps the whole pill on the screen (inside the given bounds).</summary>
    public static Box Clamp(Box pill, Box bounds)
    {
        int w = pill.Width, h = pill.Height;
        int left = Math.Clamp(pill.Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - w));
        int top = Math.Clamp(pill.Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - h));
        return new Box(left, top, left + w, top + h);
    }

    /// <summary>
    /// Keeps the whole pill on ONE real screen: the one holding its center, or else the nearest. (Clamping to the box around all screens
    /// would let it rest in a gap between screens of different sizes or positions.)
    /// </summary>
    public static Box ClampToScreens(Box pill, IReadOnlyList<Box> screens)
    {
        if (screens.Count == 0) return pill;
        int cx = (pill.Left + pill.Right) / 2, cy = (pill.Top + pill.Bottom) / 2;
        Box best = screens[0]; double bestD = double.MaxValue;
        foreach (var sc in screens)
        {
            int dx = Math.Max(Math.Max(sc.Left - cx, cx - (sc.Right - 1)), 0), dy = Math.Max(Math.Max(sc.Top - cy, cy - (sc.Bottom - 1)), 0);
            double d = (double)dx * dx + (double)dy * dy;
            if (d < bestD) { bestD = d; best = sc; }
        }
        return Clamp(pill, best);
    }

    /// <summary>Snaps to the edges of the work area when within <paramref name="threshold"/> pixels (a gentle magnet).</summary>
    public static Box Snap(Box pill, Box work, int threshold)
    {
        int left = pill.Left, top = pill.Top, w = pill.Width, h = pill.Height;
        if (Math.Abs(left - work.Left) <= threshold) left = work.Left + threshold / 2;
        else if (Math.Abs(work.Right - pill.Right) <= threshold) left = work.Right - w - threshold / 2;
        if (Math.Abs(top - work.Top) <= threshold) top = work.Top + threshold / 2;
        else if (Math.Abs(work.Bottom - pill.Bottom) <= threshold) top = work.Bottom - h - threshold / 2;
        return new Box(left, top, left + w, top + h);
    }

    /// <summary>The panel drops down beneath the pill. It opens up only when there is not room below and there is more room above.</summary>
    public static bool OpensUp(Box pill, Box work, double s)
    {
        int need = Panel(s).Height + S(10, s);
        int below = work.Bottom - pill.Bottom, above = pill.Top - work.Top;
        return below < need && above > below;
    }

    /// <summary>Where the open panel goes, kept inside the work area.</summary>
    public static Box PanelBox(Box pill, Box work, double s)
    {
        var parts = Panel(s);
        int gap = S(10, s);
        int left = Math.Clamp(pill.Left, work.Left, Math.Max(work.Left, work.Right - parts.Width));
        bool up = OpensUp(pill, work, s);
        int top = up ? pill.Top - gap - parts.Height : pill.Bottom + gap;
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - parts.Height));
        return new Box(left, top, left + parts.Width, top + parts.Height);
    }

    /// <summary>The picture window around the pill (and the open panel): the shadow needs room on every side.</summary>
    public static Box WindowRect(Box pill, Box? panel, double s)
    {
        var all = panel is { } p ? new Box(Math.Min(pill.Left, p.Left), Math.Min(pill.Top, p.Top), Math.Max(pill.Right, p.Right), Math.Max(pill.Bottom, p.Bottom)) : pill;
        return new Box(all.Left - S(ShadowSide, s), all.Top - S(ShadowTop, s), all.Right + S(ShadowSide, s), all.Bottom + S(ShadowBottom, s));
    }

    public static string NormalizeTheme(string? t) => string.Equals(t, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";

    /// <summary>The word the pill shows next to the switch.</summary>
    public static string StatusText(bool enabled, bool paused, int dimPercent) => !enabled ? "Off" : paused ? "Paused" : dimPercent + "%";
}

/// <summary>Where the full settings window goes when it is opened from the quick bar: right under the pill (or above it when there is no room below).</summary>
internal static class SettingsPlacement
{
    public static (int X, int Y) Below(Box pill, Box work, int width, int height, int gap)
    {
        int x = Math.Clamp(pill.Left, work.Left, Math.Max(work.Left, work.Right - width));
        int y = pill.Bottom + gap;
        if (y + height > work.Bottom) y = pill.Top - gap - height;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return (x, y);
    }
}
