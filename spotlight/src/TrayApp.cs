using System.Diagnostics;

namespace Spotlight;

/// <summary>The tray icon, its plain menu, and the hotkey. No main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int HotkeyId = 1;

    private sealed class HotkeyWindow : NativeWindow
    {
        public event Action? Pressed;
        public HotkeyWindow() => CreateHandle(new CreateParams { Caption = Instance.TrayTitle, ExStyle = Native.WS_EX_TOOLWINDOW });
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) { Pressed?.Invoke(); return; }
            base.WndProc(ref m);
        }
    }

    private Settings _settings;
    private readonly Engine _engine;
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _miOn, _miStartup, _miPause, _miFollow;
    private readonly System.Windows.Forms.Timer _resumeTimer = new(), _clickTimer = new(), _retryTimer = new() { Interval = 2000 };
    private Icon? _iconOn, _iconOff;
    private readonly QuickBarWindow _bar = new();
    private readonly ToolStripMenuItem _miBar;
    private readonly HotkeyWindow _hotkeyWindow = new();
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private RegisteredWaitHandle? _quitWait, _reloadWait;
    private readonly EventWaitHandle _reloadEvent = new(false, EventResetMode.AutoReset, Instance.ReloadEventName);
    private bool _hotkeyOk;

    public TrayApp(EventWaitHandle quitEvent)
    {
        _settings = Settings.Load();
        _engine = new Engine(_settings.ToOptions());
        // The file could not be read at start: stay off, and look again every couple of seconds until it can be read.
        _retryTimer.Tick += (_, _) =>
        {
            var s = Settings.Load();
            if (s.Unavailable) return;
            _retryTimer.Stop(); _settings = s; RegisterHotkey(); Apply();
        };
        if (_settings.Unavailable) _retryTimer.Start();

        _miOn = new ToolStripMenuItem("On", null, (_, _) => ToggleOn());
        _miPause = new ToolStripMenuItem("Pause for 1 hour", null, (_, _) => TogglePause());
        _miFollow = new ToolStripMenuItem("Back to following windows", null, (_, _) => FollowWindows());
        var miArea = new ToolStripMenuItem("Pick an area...", null, (_, _) => PickArea());
        _miBar = new ToolStripMenuItem("Quick bar", null, (_, _) => Change(s => s.QuickBar = !s.QuickBar));
        var miSettings = new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()) { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        _miStartup = new ToolStripMenuItem("Start at sign-in", null, (_, _) => ToggleStartup());
        _menu.Items.Add(miSettings);
        _menu.Items.Add(_miOn);
        _menu.Items.Add(_miPause);
        _menu.Items.Add(miArea);
        _menu.Items.Add(_miBar);
        _menu.Items.Add(_miFollow);
        _menu.Items.Add(_miStartup);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit Spotlight", null, (_, _) => Quit());
        _menu.Opening += (_, _) => RefreshMenu();

        _iconOn = LoadIcon();
        _iconOff = Grayed(_iconOn);
        _tray.Icon = _iconOn;
        _tray.ContextMenuStrip = _menu;
        // One click turns Spotlight on or off; a double-click opens the settings window (the click that starts a double-click is waited out).
        _clickTimer.Interval = Math.Max(150, SystemInformation.DoubleClickTime);
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); ToggleOn(); };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { _clickTimer.Stop(); _clickTimer.Start(); } };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) { _clickTimer.Stop(); OpenSettings(); } };
        _tray.Visible = true;

        _bar.ToggleRequested += ToggleOn;
        _bar.PauseRequested += TogglePause;
        _bar.SettingsRequested += () => OpenSettings(_bar.PillBox);
        _bar.HideRequested += () => Change(s => s.QuickBar = false);
        _bar.PickAreaRequested += PickArea;
        _bar.FollowRequested += FollowWindows;
        _bar.TintChosen += name => Change(s => s.Tint = name);
        _bar.Moved += (x, y) => Change(s => { s.QuickBarX = x; s.QuickBarY = y; });
        _bar.DimPreview += v => { _settings.DimPercent = v; _engine.Configure(_settings.ToOptions()); };
        _bar.DimCommitted += () => { int v = _settings.DimPercent; Change(s => s.DimPercent = v); };
        _engine.Raised += _bar.RaiseToTop;
        _hotkeyWindow.Pressed += ToggleOn;
        _resumeTimer.Tick += (_, _) => { _resumeTimer.Stop(); Reload(); };
        RegisterHotkey();
        var hk = _settings.ParsedHotkey;

        _reloadWait = ThreadPool.RegisterWaitForSingleObject(_reloadEvent, (_, _) => _ui.Post(_ => Reload(), null), null, Timeout.Infinite, false);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => _ui.Post(_ => Quit(), null), null, Timeout.Infinite, true);

        if (!Settings.IsTempCopy) Settings.RefreshStartupPath();
        _engine.HooksFailedNotice += ShowHookNotice;
        if (_engine.HooksFailed) ShowHookNotice();
        Apply();
        _engine.Evaluate(); // light the window that is in front right now

        // First time: show the settings window so it is clear what Spotlight is and how to turn it on and off.
        if (_settings.FirstRun && !Instance.IsTest) OpenSettings();
        if (Settings.TakeProblem() is { } problem && !Instance.IsTest) _tray.ShowBalloonTip(9000, "Spotlight", problem, ToolTipIcon.None);
    }

    private void RegisterHotkey()
    {
        if (_hotkeyOk) { Native.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId); _hotkeyOk = false; }
        var hk = _settings.ParsedHotkey;
        _hotkeyOk = Native.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, hk.Modifiers | 0x4000 /* MOD_NOREPEAT */, hk.VirtualKey);
        if (!_hotkeyOk) Log.Write(new InvalidOperationException($"Could not register the hotkey {hk}; another program may use it."));
    }

    /// <summary>The settings window is its own short-lived copy of Spotlight.exe; if it is already open it just comes to the front.</summary>
    private void OpenSettings(Box? near = null)
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                string args = near is { } n ? $"--settings --near={n.Left},{n.Top},{n.Right},{n.Bottom}" : "--settings";
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
            }
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    private void PickArea()
    {
        try { if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe, "--pick-area") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Write(ex); }
    }

    /// <summary>The settings window saved a change: read settings.json again and apply what differs.</summary>
    private void Reload()
    {
        var old = _settings;
        var fresh = Settings.Load();
        if (fresh.Unavailable) return; // cannot read it right now: keep what is running
        _settings = fresh;
        if (_settings.Hotkey != old.Hotkey) RegisterHotkey();
        Apply();
    }

    /// <summary>Gives the engine the current settings, and sets the timer that ends a pause.</summary>
    private void Apply()
    {
        var now = DateTime.UtcNow;
        _engine.Configure(_settings.ToOptions());
        _engine.Enabled = _settings.Active(now);
        _resumeTimer.Stop();
        if (_settings.Enabled && _settings.IsPaused(now))
        {
            _resumeTimer.Interval = (int)Math.Clamp((_settings.PausedUntilUtc!.Value - now).TotalMilliseconds + 300, 300, int.MaxValue / 2);
            _resumeTimer.Start();
        }
        RefreshMenu();
    }

    /// <summary>Saves one change on top of the newest settings.json (never over a change the settings window just made).</summary>
    private void Change(Action<Settings> change)
    {
        var (fresh, saved) = Settings.Edit(change); // read, change and save as one step (no other copy can slip an edit in between)
        if (!fresh.Unavailable) _settings = fresh;
        Apply();
        if (!saved && Settings.TakeProblem() is { } problem && !Instance.IsTest) _tray.ShowBalloonTip(8000, "Spotlight", problem, ToolTipIcon.None);
    }

    /// <summary>The shortcut and the On menu item: turn on or off; while paused they end the pause instead.</summary>
    private void ToggleOn() => Change(s => { if (s.IsPaused(DateTime.UtcNow)) s.PausedUntilUtc = null; else s.Enabled = !s.Enabled; });

    private void TogglePause() => Change(s => s.PausedUntilUtc = s.IsPaused(DateTime.UtcNow) ? null : DateTime.UtcNow.AddHours(1));

    private void FollowWindows() => Change(s => s.AreaMode = false);

    private static Icon LoadIcon()
    {
        try { if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } ico) return ico; }
        catch { }
        return SystemIcons.Application;
    }

    /// <summary>The tray icon with its colors washed out and faded: the picture for "off" and "paused".</summary>
    private static Icon Grayed(Icon source)
    {
        try
        {
            using var src = source.ToBitmap();
            using var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var m = new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new[] { .30f, .30f, .30f, 0f, 0f }, new[] { .59f, .59f, .59f, 0f, 0f }, new[] { .11f, .11f, .11f, 0f, 0f },
                    new[] { 0f, 0f, 0f, .45f, 0f }, new[] { 0f, 0f, 0f, 0f, 1f },
                });
                using var attrs = new System.Drawing.Imaging.ImageAttributes();
                attrs.SetColorMatrix(m);
                g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            IntPtr h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { Native.DestroyIcon(h); }
        }
        catch { return source; }
    }

    private void ShowHookNotice()
    {
        if (!Instance.IsTest)
            _tray.ShowBalloonTip(6000, "Spotlight", "Windows would not let Spotlight watch window changes, so it is checking a few times a second instead. It still works, but may look less smooth.", ToolTipIcon.None);
    }

    private void RefreshMenu()
    {
        var now = DateTime.UtcNow;
        bool paused = _settings.Enabled && _settings.IsPaused(now);
        _miOn.Checked = false; // the text says what a click does (Turn on / Turn off)
        _miOn.ShortcutKeyDisplayString = _hotkeyOk ? _settings.ParsedHotkey.ToString() : "";
        _miOn.ShowShortcutKeys = _hotkeyOk;
        _miPause.Enabled = _settings.Enabled;
        _miPause.Text = paused ? $"Resume now (paused until {_settings.PausedUntilUtc!.Value.ToLocalTime():t})" : "Pause for 1 hour";
        _miFollow.Visible = _settings.AreaMode;
        _miBar.Checked = _settings.QuickBar;
        _bar.Update(_settings, paused);
        _miStartup.Checked = Settings.StartsAtSignIn();
        string state = !_settings.Enabled ? "off" : paused ? "paused" : "on";
        _tray.Icon = state == "on" ? _iconOn : _iconOff; // a grayed icon when it is off or paused
        _miOn.Text = state == "on" ? "Turn off" : paused ? "Resume" : "Turn on";
        _tray.Text = $"Spotlight ({state}) - click to turn {(state == "on" ? "off" : "on")}";
    }

    private void ToggleStartup() => Settings.SetStartAtSignIn(!Settings.StartsAtSignIn());

    private void Quit()
    {
        _quitWait?.Unregister(null);
        _reloadWait?.Unregister(null);
        _reloadEvent.Dispose();
        _resumeTimer.Dispose();
        _clickTimer.Dispose();
        _bar.Dispose();
        if (_hotkeyOk) Native.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        _tray.Visible = false;
        _tray.Dispose();
        _engine.Dispose();
        _hotkeyWindow.DestroyHandle();
        ExitThread();
    }
}
