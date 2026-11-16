namespace Spotlight;

/// <summary>A rectangle in whole pixels: left/top inclusive, right/bottom exclusive. Plain data, no Windows calls.</summary>
internal readonly record struct Box(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public Box Intersect(Box o) => new(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
    public Box Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
    public bool Covers(Box o) => Left <= o.Left && Top <= o.Top && Right >= o.Right && Bottom >= o.Bottom;

    public static Box Lerp(Box a, Box b, double t) => new(
        (int)Math.Round(a.Left + (b.Left - a.Left) * t),
        (int)Math.Round(a.Top + (b.Top - a.Top) * t),
        (int)Math.Round(a.Right + (b.Right - a.Right) * t),
        (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * t));
}

/// <summary>One display. Bounds are in virtual-screen pixels (the per-monitor DPI aware ones); Dpi is that display's DPI (96 = 100%).</summary>
internal sealed record MonitorInfo(Box Bounds, int Dpi)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>The part of the spotlight on one monitor: the hole's rectangle in that monitor's own pixels, and its corner radius.</summary>
internal readonly record struct MonitorHole(int MonitorIndex, Box Local, int Radius);

internal static class Geometry
{
    /// <summary>Windows 11 rounds a window's corners by about 8 px at 100%.</summary>
    public const int CornerRadius100 = 8;

    /// <summary>
    /// The cut-out for a window: on every monitor it overlaps, the overlap in that monitor's own pixels.
    /// A maximized window has square corners; a normal one is rounded to match that monitor's scale.
    /// </summary>
    public static List<MonitorHole> Holes(IReadOnlyList<MonitorInfo> monitors, Box frame, bool maximized, int grow100 = 0)
    {
        var holes = new List<MonitorHole>();
        if (frame.IsEmpty) return holes;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            // grow100 makes the hole bigger on all sides (the soft edge's layers), scaled to this monitor; the corners grow with it.
            int grow = (int)Math.Round(grow100 * m.Scale);
            var framed = grow > 0 ? new Box(frame.Left - grow, frame.Top - grow, frame.Right + grow, frame.Bottom + grow) : frame;
            var overlap = framed.Intersect(m.Bounds);
            if (overlap.IsEmpty) continue;
            int radius = (maximized && grow == 0 ? 0 : (int)Math.Round(CornerRadius100 * m.Scale)) + grow;
            holes.Add(new MonitorHole(i, overlap.Offset(-m.Bounds.Left, -m.Bounds.Top), radius));
        }
        return holes;
    }

    /// <summary>
    /// The dimming window for a monitor: the monitor, minus its bottom pixel row. A window that covers a whole monitor makes Windows think a
    /// full-screen app is running and switch on Do Not Disturb by itself (a user saw this the moment Spotlight turned on). One row short
    /// is not "full screen", and nobody can see the missing pixel row (it sits under the taskbar or at the very edge).
    /// </summary>
    public static Box OverlayBounds(Box monitor) => new(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom - 1);

    /// <summary>The area the user picked, if it still touches a screen (a screen may have been unplugged since); otherwise null.</summary>
    public static Box? UsableArea(Box? area, IReadOnlyList<MonitorInfo> monitors)
    {
        if (area is not { } a || a.IsEmpty) return null;
        foreach (var m in monitors)
            if (!a.Intersect(m.Bounds).IsEmpty) return a;
        return null;
    }
}
