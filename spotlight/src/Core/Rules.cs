namespace Spotlight;

/// <summary>What Windows told us about a window. Plain data so the rules below can be tested with made-up windows.</summary>
internal sealed record WindowInfo(
    long Handle, int ProcessId, string ClassName, Box Frame,
    bool Visible = true, bool Minimized = false, bool Maximized = false, bool Cloaked = false, bool HasCaption = true,
    string ProcessName = "");

internal enum Verdict
{
    /// <summary>A normal window: light it.</summary>
    Spotlight,
    /// <summary>Not a window to light (taskbar, our own menu, nothing yet): leave things as they are.</summary>
    Hold,
    /// <summary>Nothing to light (desktop, full-screen app, minimized, Start, Alt+Tab): no dimming.</summary>
    Clear,
}

internal static class Rules
{
    // The taskbar and the system tray pop-ups: clicking them shouldn't flash the dimming off and on.
    private static readonly HashSet<string> HoldClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "ForegroundStaging",
    };

    // The desktop, Start, search and Alt+Tab: no dimming while they have the focus.
    private static readonly HashSet<string> ClearClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame", "TaskSwitcherWnd", "TaskSwitcherOverlayWnd",
    };

    /// <summary>Video-meeting apps (program names, lower case, no .exe). While one is in front and "meeting apps" is on, nothing is dimmed.</summary>
    public static readonly string[] MeetingApps =
    {
        "zoom", "teams", "ms-teams", "webex", "webexmta", "ciscocollabhost", "atmgr", "bluejeans", "gotomeeting", "g2mcomm", "skype", "skypeapp", "whereby",
    };

    /// <summary>
    /// A Windows notification pop-up (a toast): a small window from the shell's notification host, in the corner of a screen.
    /// The Start menu, Search and the notification center are the same kind of window but much bigger, so size tells them apart.
    /// </summary>
    public static bool IsToast(WindowInfo w, double scale = 1.0) =>
        w.ClassName == "Windows.UI.Core.CoreWindow" && w.Visible && !w.Cloaked && !w.Frame.IsEmpty
        && w.ProcessName is "shellexperiencehost" or "shellhost" or "shellexperiencehostx"
        && w.Frame.Width >= 200 * scale && w.Frame.Width <= 700 * scale && w.Frame.Height >= 40 * scale && w.Frame.Height <= 360 * scale; // sizes grow with the screen's display scale

    // The taskbar and the small pop-ups that belong to it, plus pop-up menus (right-click menus, the tray icon menus).
    private static readonly HashSet<string> TaskbarClasses = new(StringComparer.Ordinal) { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
    private static readonly HashSet<string> ShellPopupClasses = new(StringComparer.Ordinal)
    {
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "TaskListThumbnailWnd", "TaskListOverlayWnd", "Xaml_WindowedPopupClass", "#32768",
        "Microsoft.UI.Content.PopupWindowSiteBridge", // the Windows 11 right-click menus and flyouts (seen in the Sandbox: class PopupWindowSiteBridge, title PopupHost)
       
    };

    /// <summary>The taskbar (square), or one of its pop-ups or a pop-up menu (rounded), when it is on screen. These stay lit while "keep the taskbar bright" is on.</summary>
    public static ExtraLight? ShellPiece(WindowInfo w, IReadOnlyList<MonitorInfo> monitors)
    {
        if (!w.Visible || w.Cloaked || w.Frame.IsEmpty) return null;
        // A pop-up that covers a big part of a screen is not a small menu: lighting it would undo the dimming. Skip it.
        foreach (var m in monitors)
        {
            var o = w.Frame.Intersect(m.Bounds);
            if (!o.IsEmpty && (long)o.Width * o.Height * 100 > (long)m.Bounds.Width * m.Bounds.Height * 35) return null;
        }
        if (TaskbarClasses.Contains(w.ClassName)) return new ExtraLight(w.Frame, true);
        if (ShellPopupClasses.Contains(w.ClassName)) return new ExtraLight(w.Frame, false);
        return null;
    }

    public static bool IsShellClass(string cls) => TaskbarClasses.Contains(cls) || ShellPopupClasses.Contains(cls);

    public static string NormalizeApp(string name)
    {
        name = name.Trim().ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    public static Verdict Classify(WindowInfo? w, IReadOnlyList<MonitorInfo> monitors, int ownProcessId, IReadOnlySet<string>? skipApps = null)
    {
        if (w == null || w.Handle == 0) return Verdict.Hold;
        if (w.ProcessId == ownProcessId) return Verdict.Hold;
        if (HoldClasses.Contains(w.ClassName)) return Verdict.Hold;
        if (ClearClasses.Contains(w.ClassName)) return Verdict.Clear;
        if (skipApps is { Count: > 0 } && w.ProcessName.Length > 0 && skipApps.Contains(w.ProcessName)) return Verdict.Clear; // never-dim apps
        if (!w.Visible || w.Cloaked || w.Minimized || w.Frame.IsEmpty) return Verdict.Clear;
        if (IsFullScreen(w, monitors)) return Verdict.Clear;
        return Verdict.Spotlight;
    }

    /// <summary>
    /// A game or video in full screen: it covers a whole monitor (taskbar included) and has no title bar.
    /// A maximized window with an auto-hidden taskbar also covers the monitor, but it keeps its title bar, so it still gets lit.
    /// </summary>
    public static bool IsFullScreen(WindowInfo w, IReadOnlyList<MonitorInfo> monitors)
    {
        if (w.HasCaption) return false;
        foreach (var m in monitors)
            if (w.Frame.Covers(m.Bounds)) return true;
        return false;
    }
}
