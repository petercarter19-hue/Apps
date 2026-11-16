namespace Spotlight;

internal enum DecisionKind { Hold, Show, Clear }

/// <summary>What the light should do next. Animate = glide there (a click on another window); otherwise jump (the window is being dragged).</summary>
internal readonly record struct Decision(DecisionKind Kind, Box Frame = default, bool Maximized = false, bool Animate = false)
{
    public static readonly Decision Hold = new(DecisionKind.Hold);
    public static readonly Decision Clear = new(DecisionKind.Clear);
}

/// <summary>Decides, from window events, which window is lit. No Windows calls: the engine feeds it WindowInfo.</summary>
internal sealed class FollowLogic
{
    private readonly int _ownPid;
    private IReadOnlyList<MonitorInfo> _monitors;

    public FollowLogic(int ownProcessId, IReadOnlyList<MonitorInfo> monitors) { _ownPid = ownProcessId; _monitors = monitors; }

    public bool Enabled { get; set; } = true;
    /// <summary>True while a full-screen window (a game or video) is the one in front; the quick bar hides itself meanwhile.</summary>
    public bool FullScreenInFront => Target == 0 && _fullScreen != 0;
    /// <summary>Apps that are never dimmed around: while one is in front, nothing is dimmed.</summary>
    public IReadOnlySet<string>? SkipApps { get; set; }
    /// <summary>When set, the light stays on this area and ignores windows (the "pick an area" mode).</summary>
    public Box? FixedArea { get; set; }

    private Verdict Classify(WindowInfo? w) => Rules.Classify(w, _monitors, _ownPid, SkipApps);
    /// <summary>The window being lit, or 0.</summary>
    public long Target { get; private set; }

    // A window that was cleared only because it is full screen. Its size is still watched, so leaving full screen lights it again.
    private long _fullScreen;

    /// <summary>The window whose size/place events matter: the lit window, or else the full-screen window we are waiting on, or 0.</summary>
    public long Watched => Target != 0 ? Target : _fullScreen;

    /// <summary>
    /// Windows sent no foreground event (seen when a window opens while the taskbar has focus), so the engine looks at the front window
    /// now and then. True when what is in front should change what is lit: another normal window, or something that should clear the light.
    /// </summary>
    public bool NeedsRecheck(WindowInfo? w)
    {
        if (!Enabled || FixedArea != null) return false;
        switch (Classify(w))
        {
            case Verdict.Spotlight: return w!.Handle != Target;
            case Verdict.Clear: return Target != 0;
            default: return false;
        }
    }

    // The last foreground event was a window we leave alone (the taskbar, our own menu): the light stays on the old window meanwhile.
    private bool _foregroundHeld;

    /// <summary>
    /// True while the engine should poll the front window (NeedsRecheck): only while the taskbar has focus or nothing is lit.
    /// While a normal window is lit it is false, so an idle Spotlight needs no timer at all.
    /// </summary>
    public bool ShouldPoll => Enabled && FixedArea == null && (Target == 0 || _foregroundHeld);

    public void SetMonitors(IReadOnlyList<MonitorInfo> monitors) => _monitors = monitors;

    /// <summary>The focus moved to <paramref name="w"/> (null if Windows has no foreground window right now).</summary>
    public Decision OnForeground(WindowInfo? w)
    {
        _foregroundHeld = false;
        if (!Enabled) { Target = 0; _fullScreen = 0; return Decision.Clear; }
        if (FixedArea is { } area) { Target = 0; _fullScreen = 0; _foregroundHeld = false; return new Decision(DecisionKind.Show, area, false, Animate: true); }
        switch (Classify(w))
        {
            case Verdict.Spotlight:
                Target = w!.Handle;
                _fullScreen = 0;
                return new Decision(DecisionKind.Show, w.Frame, w.Maximized, Animate: true);
            case Verdict.Clear:
                Target = 0;
                _fullScreen = FullScreenHandle(w);
                return Decision.Clear;
            default:
                _foregroundHeld = true;
                return Decision.Hold;
        }
    }

    /// <summary>The lit window moved, resized, minimized or closed (w is null if it is gone). Events about any other window are ignored.</summary>
    public Decision OnTargetChanged(WindowInfo? w)
    {
        if (!Enabled) return Decision.Hold;
        if (Target == 0) return OnFullScreenChanged(w);
        if (w != null && w.Handle != Target) return Decision.Hold;
        if (w == null) { Target = 0; return Decision.Clear; }
        switch (Classify(w))
        {
            case Verdict.Spotlight:
                return new Decision(DecisionKind.Show, w.Frame, w.Maximized, Animate: false);
            case Verdict.Clear:
                Target = 0;
                _fullScreen = FullScreenHandle(w);
                return Decision.Clear;
            default:
                return Decision.Hold;
        }
    }

    /// <summary>The full-screen window we were waiting on changed: if it is a normal window now, light it again.</summary>
    private Decision OnFullScreenChanged(WindowInfo? w)
    {
        if (_fullScreen == 0) return Decision.Hold;
        if (w == null) { _fullScreen = 0; return Decision.Hold; } // it closed; the light is already clear
        if (w.Handle != _fullScreen) return Decision.Hold;
        if (Classify(w) != Verdict.Spotlight) return Decision.Hold;
        Target = w.Handle;
        _fullScreen = 0;
        return new Decision(DecisionKind.Show, w.Frame, w.Maximized, Animate: false);
    }

    private long FullScreenHandle(WindowInfo? w) => w != null && w.Handle != 0 && Rules.IsFullScreen(w, _monitors) ? w.Handle : 0;
}
