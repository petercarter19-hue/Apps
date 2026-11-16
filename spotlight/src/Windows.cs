using System.Text;

namespace Spotlight;

/// <summary>Reads real windows and monitors from Windows into the plain data the rules use.</summary>
internal static class Win
{
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            int dpi = 96;
            try { if (Native.GetDpiForMonitor(mon, Native.MDT_EFFECTIVE_DPI, out uint x, out _) == 0 && x > 0) dpi = (int)x; } catch { }
            list.Add(new MonitorInfo(new Box(r.Left, r.Top, r.Right, r.Bottom), dpi));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// The extra windows to light right now: notification pop-ups (if asked) and the taskbar with its pop-ups and menus (if asked).
    /// Only windows of those few kinds are read in full, so this is cheap even with many windows open.
    /// </summary>
    public static List<ExtraLight> ExtraLit(bool toasts, bool shell, IReadOnlyList<MonitorInfo> monitors)
    {
        var list = new List<ExtraLight>();
        var cls = new StringBuilder(64);
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            cls.Clear();
            Native.GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            bool maybeToast = toasts && c == "Windows.UI.Core.CoreWindow";
            bool maybeShell = shell && Rules.IsShellClass(c);
            if (!maybeToast && !maybeShell) return true;
            if (Read(hwnd) is not { } w) return true;
            double scale = monitors.FirstOrDefault(m => (w.Frame.Left + w.Frame.Right) / 2 >= m.Bounds.Left && (w.Frame.Left + w.Frame.Right) / 2 < m.Bounds.Right && (w.Frame.Top + w.Frame.Bottom) / 2 >= m.Bounds.Top && (w.Frame.Top + w.Frame.Bottom) / 2 < m.Bounds.Bottom)?.Scale ?? 1.0;
            if (maybeToast && Rules.IsToast(w, scale)) list.Add(new ExtraLight(w.Frame, false));
            else if (maybeShell && Rules.ShellPiece(w, monitors) is { } piece) list.Add(piece);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The program's name, lower case without .exe (for the "never dim" list); empty if Windows will not say.</summary>
    public static string NameOf(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return Rules.NormalizeApp(p.ProcessName); }
        catch { return ""; }
    }

    /// <summary>The window's visible frame (not its invisible resize borders) and flags, or null if it is gone.</summary>
    public static WindowInfo? Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var cls = new StringBuilder(256);
        Native.GetClassName(hwnd, cls, cls.Capacity);

        Native.RECT r;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, 16) != 0 && !Native.GetWindowRect(hwnd, out r))
            return null;
        int cloaked = 0;
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, 4);
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();

        return new WindowInfo(
            hwnd.ToInt64(), (int)pid, cls.ToString(), new Box(r.Left, r.Top, r.Right, r.Bottom),
            Visible: Native.IsWindowVisible(hwnd),
            Minimized: Native.IsIconic(hwnd),
            Maximized: Native.IsZoomed(hwnd),
            Cloaked: cloaked != 0,
            HasCaption: (style & Native.WS_CAPTION) == Native.WS_CAPTION,
            ProcessName: NameOf((int)pid));
    }
}
