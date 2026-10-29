using System.Diagnostics;
using WinTimer = System.Windows.Forms.Timer;

namespace Glance;

/// <summary>Owns the two tray tiles, the timers, the menu and the card. No main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly SystemStats _stats = new();
    private readonly ProcessScanner _scanner = new();
    private IconRenderer _renderer;
    private readonly TrayWindow _tray = new();
    private readonly TrayIcon _ramIcon, _cpuIcon;
    private readonly HoverCard _card = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _miCpuTile, _miStartup;

    private readonly WinTimer _tick = new() { Interval = 1000 };     // tiles
    private readonly WinTimer _bgScan = new() { Interval = 30000 };  // keeps the "biggest grower" history while the card is closed
    private readonly WinTimer _cardScan = new() { Interval = 1500 }; // list refresh while the card is open
    private readonly WinTimer _hover = new() { Interval = 350 };     // fallback hover detection
    private readonly WinTimer _poll = new() { Interval = 100 };      // mouse-left / click-away / Esc while the card is open
    private readonly WinTimer _pulse = new() { Interval = 250 };     // one short red pulse when memory crosses 90%
    private readonly WinTimer _rescan = new() { Interval = 400 };    // refresh shortly after an End

    private TrayIcon? _hoverSource;
    private DateTime _outsideSince = DateTime.MaxValue;
    private int _pulseStep = -1;
    private bool _high;
    private bool _exiting;
    private readonly bool _preview;

    public TrayApp(bool preview, bool pinnedAtStart, bool restarted)
    {
        _preview = preview;
        _settings = Settings.Load();
        _renderer = new IconRenderer(TilePx());

        _ramIcon = new TrayIcon(_tray, 1);
        _cpuIcon = new TrayIcon(_tray, 2);
        foreach (var icon in new[] { _ramIcon, _cpuIcon })
        {
            icon.MouseMoved += () => { if (!_card.Visible) { _hoverSource = icon; _hover.Stop(); _hover.Start(); } };
            icon.HoverOpen += () => OpenCard(icon, pinned: false);
            icon.Click += () => ToggleCard(icon);
            icon.ContextMenu += ShowMenu;
        }
        _tray.TaskbarCreated += () =>
        {
            _ramIcon.ReAdd();
            if (_settings.ShowCpuTile) _cpuIcon.ReAdd();
        };

        _miCpuTile = new ToolStripMenuItem("Show CPU tile", null, (_, _) => ToggleCpuTile()) { CheckOnClick = false };
        _miStartup = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        _menu.Items.Add(_miCpuTile);
        _menu.Items.Add(_miStartup);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open Task Manager", null, (_, _) => OpenTaskManager());
        _menu.Items.Add("About Glance", null, (_, _) => ShowAbout());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Remove Glance from this PC...", null, (_, _) => RemoveFromPc());
        _menu.Items.Add("Quit Glance", null, (_, _) => Quit());

        _card.KillRequested += OnKill;
        _card.KillAsAdminRequested += OnKillAsAdmin;
        _card.RestartExplorerRequested += OnRestartExplorer;
        _card.QuitRequested += Quit;
        _card.SortChanged += _ => RefreshCard();
        _card.GearClicked += ShowMenu;
        _ = _card.Handle; // creates the handle now so BeginInvoke works from the quit watcher thread

        _tick.Tick += (_, _) => Tick();
        _bgScan.Tick += (_, _) => { if (!_card.Visible) _scanner.Scan(1, SortMode.Memory); };
        _cardScan.Tick += (_, _) => RefreshCard();
        _hover.Tick += (_, _) => HoverTick();
        _poll.Tick += (_, _) => PollTick();
        _pulse.Tick += (_, _) => PulseTick();
        _rescan.Tick += (_, _) => { _rescan.Stop(); if (_card.Visible) RefreshCard(); };

        _stats.Sample();
        _scanner.Scan(1, SortMode.Memory);
        RenderIcons();
        _tick.Start();
        _bgScan.Start();
        StartQuitWatcher();

        // A copy running from a temporary folder (inside a zip, say) won't be there next time, so don't point Start or startup at it.
        if (!Settings.IsTempCopy)
        {
            Shortcut.Ensure();
            Settings.RefreshStartupPath();
        }
        if (_settings.FirstRun)
        {
            if (!Settings.IsTempCopy) Settings.SetStartWithWindows(true);
            _ramIcon.ShowBalloon("Glance is running",
                "Your memory use is the number by the clock. Don't see it? Click the ^ arrow by the clock and drag Glance onto the taskbar.");
        }
        else if (restarted) _ramIcon.ShowBalloon("Glance restarted", "It's back by the clock.");

        if (pinnedAtStart) OpenCard(_ramIcon, pinned: true);
    }

    // ---- tiles -----------------------------------------------------------------------------

    private void Tick()
    {
        _stats.Sample();
        bool high = _stats.MemPercent >= 90;
        if (high && !_high && _pulseStep < 0) { _pulseStep = 0; _pulse.Start(); }
        if (high) _high = true; else if (_stats.MemPercent < 85) _high = false;
        RenderIcons();
        if (_preview) WritePreview();
    }

    private void PulseTick()
    {
        _pulseStep++;
        if (_pulseStep > 5) { _pulseStep = -1; _pulse.Stop(); }
        RenderIcons();
    }

    /// <summary>Tile size for the taskbar's own monitor, re-checked on every render because scaling can change under us.</summary>
    private static int TilePx()
    {
        IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
        int dpi = taskbar != IntPtr.Zero ? Native.GetDpiForWindow(taskbar) : 0;
        if (dpi <= 0) dpi = Native.GetDpiForSystem();
        return Math.Max(16, (int)Math.Round(16 * dpi / 96.0));
    }

    private void RenderIcons()
    {
        int px = TilePx();
        if (px != _renderer.Size) _renderer = new IconRenderer(px);
        int mem = (int)Math.Round(_stats.MemPercent);
        int cpu = (int)Math.Round(_stats.CpuPercent);
        bool pulseOn = _pulseStep >= 0 && _pulseStep % 2 == 0;
        _ramIcon.SetIcon(_renderer.Render(mem, pulseOn), $"Memory {mem}% · {Fmt.Gb(_stats.MemUsed)} of {Fmt.Gb0(_stats.MemTotal)} GB");
        if (_settings.ShowCpuTile) _cpuIcon.SetIcon(_renderer.Render(cpu, false), $"CPU {cpu}%");
    }

    // ---- card ------------------------------------------------------------------------------

    private void OpenCard(TrayIcon source, bool pinned)
    {
        _hover.Stop();
        if (_card.Visible)
        {
            if (pinned) { _card.Pinned = true; _card.Invalidate(); }
            return;
        }
        _card.SetSort(source == _cpuIcon ? SortMode.Cpu : SortMode.Memory);
        _card.Pinned = pinned;
        RefreshCard();
        _card.ShowAt(source.GetRect());
        _outsideSince = DateTime.MaxValue;
        _cardScan.Start();
        _poll.Start();
        WritePreview();
    }

    /// <summary>With --preview, drop the card state into %LocalAppData%\Glance\preview.txt so a script can drive and check it.</summary>
    private void WritePreview()
    {
        if (!_preview) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"time={DateTime.Now:HH:mm:ss.fff}");
            sb.AppendLine($"visible={_card.Visible} pinned={_card.Pinned} bounds={_card.Bounds}");
            sb.AppendLine($"ram={_ramIcon.GetRect()} cpu={_cpuIcon.GetRect()}");
            sb.AppendLine($"mem={_stats.MemPercent:0.0} cpu={_stats.CpuPercent:0.0} grower={_scanner.GrowerText()}");
            sb.Append(_card.DebugRows());
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(Path.Combine(Settings.Dir, "preview.txt"), sb.ToString());

            if (_card.Visible)
            {
                using var bmp = new Bitmap(_card.Width, _card.Height);
                _card.DrawToBitmap(bmp, new Rectangle(0, 0, _card.Width, _card.Height));
                bmp.Save(Path.Combine(Settings.Dir, "card.png"));
            }
            SaveTilesPreview();
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    /// <summary>The two tiles at real tray size on the left, and the same pixels at 4x on the right.</summary>
    private void SaveTilesPreview()
    {
        int s = _renderer.Size, zoom = 4, gap = 8;
        int mem = (int)Math.Round(_stats.MemPercent), cpu = (int)Math.Round(_stats.CpuPercent);
        using var ram = IconRenderer.RenderBitmap(mem, false, s);
        using var cpuTile = IconRenderer.RenderBitmap(cpu, false, s);
        int w = 8 + s * 2 + gap + 24 + s * zoom * 2 + gap * zoom + 8, h = s * zoom + 16;
        using var canvas = new Bitmap(w, h);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(243, 243, 243));
        g.DrawImageUnscaled(ram, 8, (h - s) / 2);
        g.DrawImageUnscaled(cpuTile, 8 + s + gap, (h - s) / 2);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        int x = 8 + s * 2 + gap + 24;
        g.DrawImage(ram, new Rectangle(x, 8, s * zoom, s * zoom));
        g.DrawImage(cpuTile, new Rectangle(x + s * zoom + gap * zoom, 8, s * zoom, s * zoom));
        canvas.Save(Path.Combine(Settings.Dir, "tiles.png"));
    }

    private void ToggleCard(TrayIcon source)
    {
        if (!_card.Visible) { OpenCard(source, pinned: true); return; }
        if (_card.Pinned) CloseCard();
        else { _card.Pinned = true; _card.Invalidate(); }
    }

    private void CloseCard()
    {
        _card.Pinned = false;
        _card.Hide();
        _cardScan.Stop();
        _poll.Stop();
        WritePreview();
    }

    private void RefreshCard()
    {
        var rows = _scanner.Scan(HoverCard.TopN, _card.Sort);
        _card.UpdateData(rows, _stats, _scanner.GrowerText());
        if (_card.Visible) WritePreview();
    }

    private void HoverTick()
    {
        _hover.Stop();
        if (_card.Visible || _hoverSource == null || _menu.Visible) return;
        if (_hoverSource.GetRect().Contains(Cursor.Position)) OpenCard(_hoverSource, pinned: false);
    }

    private void PollTick()
    {
        if (!_card.Visible || _menu.Visible) return;
        var p = Cursor.Position;
        var near = _card.Bounds;
        near.Inflate(10, 10);
        bool inside = near.Contains(p) || _ramIcon.GetRect().Contains(p) || _cpuIcon.GetRect().Contains(p);

        if (_card.Pinned)
        {
            bool mouseDown = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 || (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
            bool esc = (Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0;
            if ((mouseDown && !inside) || esc) CloseCard();
            return;
        }

        if (inside) { _outsideSince = DateTime.MaxValue; return; }
        if (_outsideSince == DateTime.MaxValue) _outsideSince = DateTime.UtcNow;
        else if (DateTime.UtcNow - _outsideSince > TimeSpan.FromMilliseconds(450)) CloseCard();
    }

    // ---- actions ---------------------------------------------------------------------------

    private void OnKill(ProcessGroup g)
    {
        var tree = ProcessKiller.CollectTree(g.Pids, _scanner.Procs);
        var r = ProcessKiller.Kill(tree);
        if (r.Denied > 0) _card.MarkNeedsAdmin(g.Key);
        _rescan.Start();
    }

    private async void OnKillAsAdmin(ProcessGroup g)
    {
        try
        {
            var tree = ProcessKiller.CollectTree(g.Pids, _scanner.Procs);
            int? refused = await Task.Run(() => ProcessKiller.KillElevated(tree));
            if (_exiting) return;
            if (_card.Visible) RefreshCard();
            if (refused > 0) _card.MarkFailed(g.Key); // null means the UAC prompt was cancelled: leave the row as it was
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    private void OnRestartExplorer(ProcessGroup g)
    {
        CloseCard();
        ProcessKiller.RestartExplorer(g.Pids, _scanner.Procs);
    }

    private void ShowMenu(Point at)
    {
        _hover.Stop();
        _miCpuTile.Checked = _settings.ShowCpuTile;
        _miStartup.Checked = Settings.StartsWithWindows();
        Native.SetForegroundWindow(_tray.Handle);
        _menu.Show(at);
    }

    private void ToggleCpuTile()
    {
        _settings.ShowCpuTile = !_settings.ShowCpuTile;
        _settings.Save();
        if (_settings.ShowCpuTile) RenderIcons(); else _cpuIcon.Remove();
    }

    private void ToggleStartup() => Settings.SetStartWithWindows(!Settings.StartsWithWindows());

    private static void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose(); } catch { }
    }

    private void ShowAbout()
    {
        string version = typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        MessageBox.Show(
            $"Glance {version}\n\nMemory and CPU in the tray. Hover a tile for the top ten apps; End is two clicks.\n\n{Environment.ProcessPath}\nSettings: {Settings.Dir}",
            "About Glance", MessageBoxButtons.OK, MessageBoxIcon.None);
    }

    private void RemoveFromPc()
    {
        var answer = MessageBox.Show(
            "This turns off Start with Windows, removes Glance from the Start menu, deletes its settings, and closes it.\n\n" +
            $"After that you can delete the app file itself:\n{Environment.ProcessPath}",
            "Remove Glance from this PC", MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        Settings.RemoveAll();
        Quit();
    }

    private void StartQuitWatcher()
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
                ev.WaitOne();
                if (!_exiting) _card.BeginInvoke(Quit);
            }
            catch (Exception ex) { Log.Write(ex); }
        }) { IsBackground = true, Name = "Glance quit watcher" };
        thread.Start();
    }

    private void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        _tick.Stop(); _bgScan.Stop(); _cardScan.Stop(); _hover.Stop(); _poll.Stop(); _pulse.Stop(); _rescan.Stop();
        _card.Hide();
        _ramIcon.Dispose();
        _cpuIcon.Dispose();
        ExitThread();
    }
}
