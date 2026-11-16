using System.Diagnostics;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace Spotlight;

/// <summary>
/// Ties the pieces to Windows: one overlay per monitor, the window-event hooks, and a 16 ms timer that runs only while
/// something is moving (a glide, a fade, or a window being dragged). Idle, it uses no timer at all.
/// </summary>
internal sealed class Engine : IDisposable
{
    private readonly FollowLogic _follow;
    private readonly SpotlightModel _model = new();
    private readonly List<OverlayWindow> _overlays = new();
    private List<MonitorInfo> _monitors;
    private readonly WinTimer _frame = new() { Interval = 16 };
    private readonly WinTimer _displayDebounce = new() { Interval = 600 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<IntPtr> _hooks = new();
    private readonly Native.WinEventProc _hookProc; // kept in a field so it is never collected while Windows holds it
    private bool _targetDirty;
    private EngineOptions _o;
    private readonly WinTimer _toastPoll = new() { Interval = 150 };
    private List<ExtraLight> _extras = new();
    private readonly List<(int Monitor, int Layer)> _overlayAt = new();

    public Engine(EngineOptions options)
    {
        _o = options;
        _monitors = Win.Monitors();
        _follow = new FollowLogic(Environment.ProcessId, _monitors) { SkipApps = _o.SkipApps };
        _follow.FixedArea = Geometry.UsableArea(_o.Area, _monitors);
        BuildOverlays();

        _hookProc = OnWinEvent;
        bool ok = Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0);
        ok &= Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, 0);
        ok &= Hook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_DESTROY, 0);
        // Menus (right-click menus, tray menus) appear and vanish quickly: look for them at once instead of waiting for the next tick.
        Hook(Native.EVENT_SYSTEM_MENUPOPUPSTART, Native.EVENT_SYSTEM_MENUPOPUPEND, 0);
        // The location hook is attached only to the lit window's own thread (see Evaluate), so mouse movement and other windows never call us.
        if (!ok)
        {
            // Fall back to looking at the front window a few times a second; it still works, just less smoothly.
            HooksFailed = true;
            Log.WriteOnce(new InvalidOperationException("SetWinEventHook failed; falling back to polling the front window."));
            _poll.Tick += (_, _) => Safe(() => { Evaluate(); });
            _poll.Start();
        }

        // Windows sometimes sends no foreground event (a window opened while the taskbar had focus). While the taskbar has focus or nothing
        // is lit, look at the front window a few times a second; the timer is off whenever a normal window is lit (see FollowLogic.ShouldPoll).
        _recheck.Tick += (_, _) => Safe(Recheck);
        UpdateRecheck();

        _toastPoll.Tick += (_, _) => Safe(CheckExtras);
        _frame.Tick += (_, _) => Safe(Frame, stopFrames: true);
        _displayDebounce.Tick += (_, _) => { _displayDebounce.Stop(); RebuildForDisplayChange(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool Enabled
    {
        get => _follow.Enabled;
        set
        {
            if (_follow.Enabled == value) return;
            _follow.Enabled = value;
            Evaluate(); // on: light whatever is in front now; off: fade out
        }
    }

    /// <summary>Applies new settings: how dark, color, soft edge, one screen, hidden from capture, never-dim apps, picked area.</summary>
    public void Configure(EngineOptions o)
    {
        var old = _o;
        _o = o;
        _follow.SkipApps = o.SkipApps;
        var area = Geometry.UsableArea(o.Area, _monitors);
        bool followChanged = area != _follow.FixedArea || old.SkipApps.Count != o.SkipApps.Count || !old.SkipApps.All(o.SkipApps.Contains);
        _follow.FixedArea = area;
        if (o.SoftEdge != old.SoftEdge) BuildOverlays(); else ApplyLook();
        if (followChanged) Evaluate(); // a different area or app list can change what is lit right now
        UpdateToastPoll();
        Render();
    }

    private bool ExtrasWanted => _o.LightNotifications || _o.KeepShellBright;

    /// <summary>Extra lit windows (taskbar, tray pop-ups, menus, notifications) are looked for only while one of those options is on and something is dimmed; otherwise there is no timer.</summary>
    private void UpdateToastPoll()
    {
        bool want = ExtrasWanted && _model.Rect is not null && _model.Opacity > 0;
        if (want) { if (!_toastPoll.Enabled) _toastPoll.Start(); }
        else { if (_toastPoll.Enabled) _toastPoll.Stop(); if (_extras.Count > 0) _extras = new(); }
    }

    private void CheckExtras()
    {
        var now = Win.ExtraLit(_o.LightNotifications, _o.KeepShellBright, _monitors);
        if (now.SequenceEqual(_extras)) return;
        _extras = now;
        Render();
    }

    private void ApplyLook()
    {
        foreach (var o in _overlays) { o.SetColor(Tints.ColorRef(_o.Tint)); o.SetHiddenFromCapture(_o.HideFromCapture); }
    }

    /// <summary>True if the window-event hooks could not be set and Spotlight is polling instead (the tray shows a notice once).</summary>
    public bool HooksFailed { get; private set; }
    public event Action? HooksFailedNotice;
    /// <summary>Raised after the dimming windows were shown or raised, so the quick bar can raise itself above them again.</summary>
    public event Action? Raised;
    /// <summary>A full-screen window is in front (the quick bar hides).</summary>
    public bool FullScreenInFront => _follow.FullScreenInFront;
    public event Action? FrontChanged;

    private readonly WinTimer _poll = new() { Interval = 300 };
    private readonly WinTimer _recheck = new() { Interval = 250 };
    private IntPtr _locationHook;
    private uint _locationThread;
    private readonly HashSet<string> _failed = new();

    private bool Hook(uint min, uint max, uint thread)
    {
        // Out of context: the callback runs on this thread's message loop, never in another program.
        IntPtr h = Native.SetWinEventHook(min, max, IntPtr.Zero, _hookProc, 0, thread, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (h == IntPtr.Zero) return false;
        _hooks.Add(h);
        return true;
    }

    /// <summary>Points the location hook at the lit window's thread only (none when nothing is lit).</summary>
    private void RetargetLocationHook()
    {
        uint tid = 0;
        if (_follow.Watched != 0) tid = Native.GetWindowThreadProcessId((IntPtr)_follow.Watched, out _);
        if (tid == _locationThread && (tid == 0 || _locationHook != IntPtr.Zero)) return;
        if (_locationHook != IntPtr.Zero) { Native.UnhookWinEvent(_locationHook); _hooks.Remove(_locationHook); _locationHook = IntPtr.Zero; }
        _locationThread = tid;
        if (tid == 0) return;
        _locationHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _hookProc,
            0, tid, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_locationHook != IntPtr.Zero) _hooks.Add(_locationHook);
        else if (!HooksFailed)
        {
            HooksFailed = true;
            Log.WriteOnce(new InvalidOperationException("SetWinEventHook (location) failed; falling back to polling the front window."));
            HooksFailedNotice?.Invoke();
            _poll.Tick += (_, _) => Safe(() => { _targetDirty = true; StartFrames(); });
            _poll.Start();
        }
    }

    /// <summary>Runs one step; an error is logged once per kind, the frame timer stops and the dimming hides, instead of repeating 60 times a second.</summary>
    private void Safe(Action step, bool stopFrames = false)
    {
        try { step(); }
        catch (Exception ex)
        {
            if (_failed.Add(ex.GetType().FullName + ":" + ex.Message)) Log.WriteOnce(ex);
            if (stopFrames) _frame.Stop();
            try { foreach (var o in _overlays) o.Hide(); } catch { }
        }
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) =>
        Safe(() => HandleWinEvent(evt, hwnd, idObject, idChild));

    private void HandleWinEvent(uint evt, IntPtr hwnd, int idObject, int idChild)
    {
        if (evt == Native.EVENT_SYSTEM_MENUPOPUPSTART || evt == Native.EVENT_SYSTEM_MENUPOPUPEND) { if (ExtrasWanted && _model.Rect is not null) CheckExtras(); return; }
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF || hwnd == IntPtr.Zero) return;
        if (evt == Native.EVENT_SYSTEM_FOREGROUND) { Evaluate(); return; }
        // Everything else matters only for the lit window; for all others this returns at once.
        if (hwnd.ToInt64() != _follow.Watched) return; // the lit window, or the full-screen window we wait on
        _targetDirty = true;
        StartFrames();
    }

    /// <summary>No foreground event arrived but a different window is in front: light it (the decision is in FollowLogic.NeedsRecheck).</summary>
    private void Recheck()
    {
        if (_follow.NeedsRecheck(Win.Read(Native.GetForegroundWindow()))) Evaluate();
    }

    private void UpdateRecheck()
    {
        if (_follow.ShouldPoll) { if (!_recheck.Enabled) _recheck.Start(); }
        else if (_recheck.Enabled) _recheck.Stop();
    }

    /// <summary>Light whatever window is in front right now.</summary>
    public void Evaluate()
    {
        var d = _follow.OnForeground(Win.Read(Native.GetForegroundWindow()));
        Apply(d);
        RetargetLocationHook();
        UpdateRecheck();
        foreach (var o in _overlays) o.RaiseToTop();
        Raised?.Invoke();
        FrontChanged?.Invoke();
    }

    private void Apply(Decision d)
    {
        if (d.Kind == DecisionKind.Hold) return;
        _model.Apply(d, _clock.ElapsedMilliseconds);
        StartFrames();
        if (!_frame.Enabled) Render();
    }

    private void StartFrames()
    {
        if (!_frame.Enabled) _frame.Start();
    }

    private void Frame()
    {
        if (_targetDirty)
        {
            _targetDirty = false;
            var d = _follow.OnTargetChanged(Win.Read((IntPtr)_follow.Watched));
            if (d.Kind != DecisionKind.Hold) _model.Apply(d, _clock.ElapsedMilliseconds); // moved, resized, minimized, closed, or left full screen
            UpdateRecheck(); // nothing lit any more: poll; lit again after full screen: stop
            FrontChanged?.Invoke(); // a window went full screen or came back: the quick bar hides or shows
        }
        if (_follow.Target == 0) RetargetLocationHook();
        bool more = _model.Advance(_clock.ElapsedMilliseconds);
        Render();
        if (!more && !_targetDirty) _frame.Stop();
    }

    private void Render()
    {
        double opacity = _model.Opacity;
        UpdateToastPoll();
        if (_model.Rect is null || opacity <= 0)
        {
            foreach (var o in _overlays) o.Hide();
            return;
        }
        var rect = _model.Rect.Value;
        double level = Strengths.Level(_o.DimPercent) * Math.Clamp(opacity, 0, 1);
        // Soft edge: several thin layers with slightly bigger holes; otherwise one layer with the window-sized hole.
        var plan = _o.SoftEdge ? SoftEdge.Plan(level) : new (int Grow, double Alpha)[] { (0, level) };
        var baseHoles = Geometry.Holes(_monitors, rect, _model.Maximized);
        var layerHoles = new List<MonitorHole>?[plan.Length];
        bool newlyShown = false;
        for (int k = 0; k < _overlays.Count; k++)
        {
            var o = _overlays[k];
            var (mi, li) = _overlayAt[k];
            if (_o.OneScreenOnly && !baseHoles.Any(h => h.MonitorIndex == mi)) { o.Hide(); continue; } // dim only the screen the light is on
            if (layerHoles[li] is null)
            {
                var all = Geometry.Holes(_monitors, rect, _model.Maximized, plan[li].Grow);
                foreach (var x in _extras) all.AddRange(Geometry.Holes(_monitors, x.Frame, x.Square, plan[li].Grow)); // taskbar, pop-ups, menus, notifications are lit too
                layerHoles[li] = all;
            }
            o.SetHoles(layerHoles[li]!.Where(h => h.MonitorIndex == mi).ToList());
            o.SetAlpha((byte)Math.Clamp((int)Math.Round(plan[li].Alpha * 255), 0, 255));
            if (!o.Shown) newlyShown = true;
            o.Show();
        }
        if (newlyShown) Raised?.Invoke(); // the quick bar goes back above the dimming
    }

    // ---- displays ------------------------------------------------------------------------------

    private void BuildOverlays()
    {
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
        _overlayAt.Clear();
        int layers = _o.SoftEdge ? SoftEdge.LayerCount : 1;
        for (int mi = 0; mi < _monitors.Count; mi++)
            for (int li = 0; li < layers; li++)
            {
                _overlays.Add(new OverlayWindow(Geometry.OverlayBounds(_monitors[mi].Bounds))); // one pixel row short of full screen, so Windows does not turn on Do Not Disturb
                _overlayAt.Add((mi, li));
            }
        ApplyLook();
    }

    private void OnDisplaySettingsChanged(object? s, EventArgs e)
    {
        // Settings changes arrive in bursts; wait for them to settle.
        _displayDebounce.Stop();
        _displayDebounce.Start();
    }

    private void RebuildForDisplayChange()
    {
        _monitors = Win.Monitors();
        _follow.SetMonitors(_monitors);
        _follow.FixedArea = Geometry.UsableArea(_o.Area, _monitors);
        BuildOverlays();
        Evaluate();
        Render();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _toastPoll.Stop(); _toastPoll.Dispose(); _frame.Stop(); _displayDebounce.Stop(); _poll.Stop(); _poll.Dispose(); _recheck.Stop(); _recheck.Dispose();
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
        _frame.Dispose(); _displayDebounce.Dispose();
    }
}
