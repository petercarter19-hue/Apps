using System.Diagnostics;

namespace Spotlight;

/// <summary>
/// The settings window. Basics: on/off, how dark, color, the shortcut, pause, start at sign-in. Where: follow the window or a picked
/// area, one screen only, soft edge. Apps: never-dim programs, meeting apps, hide from screen sharing.
/// It runs as its own short-lived Spotlight.exe (started by "Settings..." or a second launch), so it is an ordinary window in front:
/// Spotlight lights it like any other, and changes show live on every other window. It saves to settings.json and pokes the
/// tray copy (the reload event), which applies the change at once. It closes itself if the tray copy goes away.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const int TestHotkeyId = 99;
    private static readonly Color Muted = Color.FromArgb(110, 110, 115);

    /// <summary>The window follows the quick bar's look: light glass = a light window, dark glass = a dark window.</summary>
    private sealed record Colors(Color Back, Color Page, Color Text, Color Muted, Color Control, Color Border, Color Good, Color Bad, bool Dark);
    private static readonly Colors LightC = new(Color.White, Color.White, Color.Black, Muted, Color.White, Color.FromArgb(173, 173, 173), Color.FromArgb(22, 128, 61), Color.FromArgb(185, 28, 28), false);
    private static readonly Colors DarkC = new(Color.FromArgb(31, 33, 37), Color.FromArgb(37, 39, 44), Color.FromArgb(243, 243, 243), Color.FromArgb(169, 172, 178), Color.FromArgb(48, 50, 56), Color.FromArgb(84, 87, 96), Color.FromArgb(96, 200, 130), Color.FromArgb(248, 113, 113), true);
    private Colors _c = LightC;
    private string _appliedTheme = "Light";
    private Box? _near;
    private static readonly Font Bold = new("Segoe UI Semibold", 10f);

    /// <summary>
    /// A tab control that can paint itself in the dark look (the system's own drawing leaves a white frame and header strip).
    /// In the light look it uses Windows' normal drawing, exactly as before.
    /// </summary>
    private sealed class ThemedTabs : TabControl
    {
        private bool _dark; private Colors _c = LightC;
        public void Theme(Colors c)
        {
            _c = c; _dark = c.Dark;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, _dark);
            UpdateStyles(); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (!_dark) { base.OnPaint(e); return; }
            var g = e.Graphics; g.Clear(_c.Back);
            var page = DisplayRectangle; page.Inflate(2, 2);
            using (var pen = new Pen(_c.Border, 1f)) g.DrawRectangle(pen, page.X, page.Y, page.Width - 1, page.Height - 1);
            for (int i = 0; i < TabCount; i++)
            {
                var r = GetTabRect(i); bool sel = i == SelectedIndex;
                using (var b = new SolidBrush(sel ? _c.Page : _c.Back)) g.FillRectangle(b, r);
                using (var pen = new Pen(_c.Border, 1f)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                if (sel) using (var b = new SolidBrush(_c.Page)) g.FillRectangle(b, r.X + 1, r.Bottom - 1, r.Width - 2, 3); // joins the tab to its page
                TextRenderer.DrawText(g, TabPages[i].Text, Font, r, sel ? _c.Text : _c.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>A small round color button; the chosen one has a ring.</summary>
    private sealed class Swatch : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color Fill { get; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Chosen { get; set; }
        public Swatch(string name, Color fill) { Fill = fill; Size = new Size(34, 34); Margin = new Padding(0, 4, 10, 4); Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.RadioButton; AccessibleName = name + " color"; SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Color.Transparent; }
        // Reachable and usable from the keyboard too: Tab to it, Space or Enter picks it.
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Fill);
            e.Graphics.FillEllipse(b, 5, 5, Width - 11, Height - 11);
            if (Focused) { using var ring = new Pen(Color.FromArgb(120, 120, 125), 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }; e.Graphics.DrawEllipse(ring, 3f, 3f, Width - 8, Height - 8); }
            if (Chosen) { using var pen = new Pen(Color.FromArgb(0, 120, 212), 2.5f); e.Graphics.DrawEllipse(pen, 1.5f, 1.5f, Width - 4, Height - 4); }
        }
    }

    private readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), Anchor = AnchorStyles.Left };
    private readonly Button _toggle = new() { AutoSize = true, MinimumSize = new Size(100, 34), Anchor = AnchorStyles.Right, Text = "Turn off" };
    private readonly Label _percent = new() { AutoSize = true, Anchor = AnchorStyles.Right, Font = Bold };
    private readonly TrackBar _slider = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, LargeChange = 10, SmallChange = 1, Dock = DockStyle.Fill, AutoSize = false, Height = 40 };
    private readonly List<Swatch> _swatches = new();
    private readonly TextBox _hotkeyBox = new() { ReadOnly = true, TextAlign = HorizontalAlignment.Center, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
    private readonly Button _change = new() { AutoSize = true, MinimumSize = new Size(100, 30), Text = "Change", Anchor = AnchorStyles.Right };
    private readonly Label _hotkeyNote = new() { AutoSize = true, ForeColor = Muted, Visible = false, MaximumSize = new Size(370, 0) };
    private readonly Button _pause = new() { AutoSize = true, MinimumSize = new Size(150, 30), Text = "Pause for 1 hour" };
    private readonly Label _pauseNote = new() { AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left };
    private readonly CheckBox _barOn = new() { AutoSize = true, Text = "Show the quick bar (a small floating pill)" };
    private readonly RadioButton _themeDark = new() { AutoSize = true, Text = "Dark glass" };
    private readonly RadioButton _themeLight = new() { AutoSize = true, Text = "Light glass" };
    private readonly CheckBox _startup = new() { AutoSize = true, Text = "Start Spotlight when I sign in" };
    private readonly RadioButton _follow = new() { AutoSize = true, Text = "Follow the window I am in" };
    private readonly RadioButton _area = new() { AutoSize = true, Text = "Only an area I pick" };
    private readonly Button _pickArea = new() { AutoSize = true, MinimumSize = new Size(120, 30), Text = "Pick area..." };
    private readonly Label _areaNote = new() { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(370, 0) };
    private readonly CheckBox _oneScreen = new() { AutoSize = true, Text = "Dim only the screen the light is on" };
    private readonly CheckBox _soft = new() { AutoSize = true, Text = "Soft glowing edge around the lit window" };
    private readonly Label _appsHint = new() { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(370, 0), Margin = new Padding(0, 0, 0, 4), Tag = "muted", Font = new Font("Segoe UI", 9f) };
    private readonly ListBox _apps = new() { Height = 96, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _running = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _addApp = new() { Text = "Add", AutoSize = true, MinimumSize = new Size(80, 28) };
    private readonly Button _removeApp = new() { Text = "Remove selected", AutoSize = true, MinimumSize = new Size(130, 28) };
    private readonly CheckBox _shell = new() { AutoSize = true, Text = "Keep the taskbar and its pop-ups bright", MaximumSize = new Size(370, 0) };
    private readonly CheckBox _notify = new() { AutoSize = true, Text = "Keep pop-up notifications bright", MaximumSize = new Size(370, 0) };
    private readonly CheckBox _meetings = new() { AutoSize = true, Text = "Do not dim during video meetings", MaximumSize = new Size(370, 0) };
    private readonly CheckBox _hideCapture = new() { AutoSize = true, Text = "Hide the dimming from screen sharing", MaximumSize = new Size(370, 0) };
    private readonly ThemedTabs _tabs = new() { Size = new Size(420, 412), Margin = new Padding(0, 14, 0, 0) };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _saveSoon = new() { Interval = 80 };
    private DateTime _fileStamp;
    private bool _loading, _capturing, _lastPaused;
    private Settings _view = new();

    /// <summary>Opens the settings window, or brings the one that is already open to the front.</summary>
    public static void ShowOrFocus(Box? near = null)
    {
        using var mutex = new Mutex(true, Instance.SettingsMutexName, out bool first);
        if (!first)
        {
            IntPtr existing = Native.FindWindowW(null, Instance.SettingsTitle);
            if (existing != IntPtr.Zero)
            {
                if (Native.IsIconic(existing)) Native.ShowWindow(existing, Native.SW_RESTORE);
                Native.SetForegroundWindow(existing);
            }
            return;
        }
        try { Application.Run(new SettingsForm(near)); }
        finally { mutex.ReleaseMutex(); }
    }

    private SettingsForm(Box? near)
    {
        _near = near;
        Text = Instance.SettingsTitle;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = near is null ? FormStartPosition.CenterScreen : FormStartPosition.Manual;
        if (near is { } nb) Location = new Point(nb.Left, nb.Bottom + 10); // moved to its final place once its size is known (OnShown)
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        KeyPreview = true;
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch { }

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(24, 18, 24, 18), Dock = DockStyle.Fill };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        Controls.Add(root);

        root.Controls.Add(new Label { Text = "Spotlight", AutoSize = true, Font = new Font("Segoe UI Semibold", 20f), Margin = new Padding(0) });
        root.Controls.Add(new Label { Text = "Dims everything except the window you are in. Click any other window to see the light move.", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 12) });

        var power = Row();
        power.Controls.Add(_status, 0, 0);
        power.Controls.Add(_toggle, 1, 0);
        root.Controls.Add(power);

        root.Controls.Add(_tabs);
        _tabs.TabPages.Add(BasicsPage());
        _tabs.TabPages.Add(WherePage());
        _tabs.TabPages.Add(AppsPage());
        _tabs.TabPages.Add(FocusPage());

        root.Controls.Add(new Label
        {
            Text = "You can close this window. Spotlight keeps running; its icon is by the clock (try the ^ arrow). Click the icon once to turn Spotlight on or off; double-click it to open this window again.",
            AutoSize = true, ForeColor = Muted, MaximumSize = new Size(410, 0), Margin = new Padding(0, 12, 0, 0),
        });

        var buttons = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var quit = new Button { Text = "Quit Spotlight", AutoSize = true, MinimumSize = new Size(120, 34), Anchor = AnchorStyles.Left };
        var done = new Button { Text = "Done", AutoSize = true, MinimumSize = new Size(100, 34), Anchor = AnchorStyles.Right };
        buttons.Controls.Add(quit, 0, 0);
        buttons.Controls.Add(done, 1, 0);
        root.Controls.Add(buttons);
        AcceptButton = done;
        CancelButton = done; // Esc closes the window (while a new shortcut is being recorded, Esc cancels that first)

        _toggle.Click += (_, _) => Persist(s => { if (s.IsPaused(DateTime.UtcNow)) s.PausedUntilUtc = null; else s.Enabled = !s.Enabled; });
        _slider.ValueChanged += (_, _) => { if (_loading) return; UpdatePercentLabel(); _saveSoon.Stop(); _saveSoon.Start(); };
        _slider.MouseCaptureChanged += (_, _) => FlushSlider();
        _saveSoon.Tick += (_, _) => FlushSlider();
        for (int i = 0; i < _swatches.Count; i++)
        {
            string name = Tints.Names[i];
            _swatches[i].Click += (_, _) => Persist(s => s.Tint = name);
        }
        _change.Click += (_, _) => (_capturing ? (Action)EndCapture : BeginCapture)();
        _pause.Click += (_, _) => Persist(s => s.PausedUntilUtc = s.IsPaused(DateTime.UtcNow) ? null : DateTime.UtcNow.AddHours(1));
        _barOn.CheckedChanged += (_, _) => { if (!_loading) Persist(s => s.QuickBar = _barOn.Checked); };
        _themeDark.CheckedChanged += (_, _) => { if (!_loading && _themeDark.Checked) Persist(s => s.QuickBarTheme = "Dark"); };
        _themeLight.CheckedChanged += (_, _) => { if (!_loading && _themeLight.Checked) Persist(s => s.QuickBarTheme = "Light"); };
        _startup.CheckedChanged += (_, _) => { if (!_loading) Settings.SetStartAtSignIn(_startup.Checked); };
        _follow.CheckedChanged += (_, _) => { if (!_loading && _follow.Checked) Persist(s => s.AreaMode = false); };
        _area.CheckedChanged += (_, _) =>
        {
            if (_loading || !_area.Checked) return;
            if (_view.HasArea) Persist(s => s.AreaMode = true); else DoPick();
        };
        _pickArea.Click += (_, _) => DoPick();
        _oneScreen.CheckedChanged += (_, _) => { if (!_loading) Persist(s => s.OneScreenOnly = _oneScreen.Checked); };
        _soft.CheckedChanged += (_, _) => { if (!_loading) Persist(s => s.SoftEdge = _soft.Checked); };
        _shell.CheckedChanged += (_, _) => { if (!_loading) Persist(s => s.KeepShellBright = _shell.Checked); };
        _notify.CheckedChanged += (_, _) => { if (!_loading) Persist(s => s.LightNotifications = _notify.Checked); };
        _meetings.CheckedChanged += (_, _) => { if (!_loading) Persist(s => s.SkipMeetingApps = _meetings.Checked); };
        _hideCapture.CheckedChanged += (_, _) => { if (!_loading) Persist(s => s.HideFromCapture = _hideCapture.Checked); };
        _addApp.Click += (_, _) => AddApp();
        _removeApp.Click += (_, _) => RemoveApp();
        _running.DropDown += (_, _) => FillRunning();
        _running.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddApp(); e.SuppressKeyPress = true; } };
        quit.Click += (_, _) => { using var q = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.QuitEventName); q.Set(); Close(); };
        done.Click += (_, _) => Close();
        Deactivate += (_, _) => { if (_capturing) EndCapture(); };
        _watch.Tick += (_, _) => Watch();

        Reload();
        _watch.Start();
    }

    // ---- the three pages -----------------------------------------------------------------------

    private static TableLayoutPanel Page()
    {
        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8, 6, 8, 6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static TabPage Wrap(string title, TableLayoutPanel content)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(4), AutoScroll = true };
        page.Controls.Add(content);
        return page;
    }

    private static readonly Font Small = new("Segoe UI", 9f);
    private static Label Note(string text) => new() { Text = text, AutoSize = true, ForeColor = Muted, Font = Small, MaximumSize = new Size(370, 0), Margin = new Padding(20, 0, 0, 2) };

    private static Label Head(string text, int top = 10) => new() { Text = text, AutoSize = true, Font = Bold, Margin = new Padding(0, top, 0, 2) };

    private TabPage BasicsPage()
    {
        var p = Page();
        var darkHead = Row();
        darkHead.Controls.Add(new Label { Text = "How dark", AutoSize = true, Anchor = AnchorStyles.Left, Font = Bold }, 0, 0);
        darkHead.Controls.Add(_percent, 1, 0);
        p.Controls.Add(darkHead);
        _slider.Margin = new Padding(0);
        p.Controls.Add(_slider);

        p.Controls.Add(Head("Color of the dimming", 4));
        var colors = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var fills = new[] { Color.Black, Color.FromArgb(120, 70, 10), Color.FromArgb(30, 60, 160), Color.FromArgb(20, 120, 60), Color.FromArgb(110, 40, 160) };
        for (int i = 0; i < Tints.Names.Length; i++)
        {
            var sw = new Swatch(Tints.Names[i], fills[i]);
            _swatches.Add(sw);
            colors.Controls.Add(sw);
        }
        p.Controls.Add(colors);

        p.Controls.Add(Head("Shortcut to turn Spotlight on or off", 8));
        var keyRow = Row();
        keyRow.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
        keyRow.Controls.Add(_hotkeyBox, 0, 0);
        keyRow.Controls.Add(_change, 1, 0);
        _hotkeyBox.Margin = new Padding(0, 3, 8, 3);
        p.Controls.Add(keyRow);
        _hotkeyNote.Margin = new Padding(0, 2, 0, 0);
        p.Controls.Add(_hotkeyNote);

        var pauseRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        pauseRow.Controls.Add(_pause);
        _pauseNote.Margin = new Padding(10, 7, 0, 0);
        pauseRow.Controls.Add(_pauseNote);
        p.Controls.Add(pauseRow);

        p.Controls.Add(Head("Quick bar", 12));
        _barOn.Margin = new Padding(0, 2, 0, 2);
        p.Controls.Add(_barOn);
        var themeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(20, 0, 0, 0) };
        themeRow.Controls.Add(_themeDark); _themeLight.Margin = new Padding(12, 3, 3, 3); themeRow.Controls.Add(_themeLight);
        p.Controls.Add(themeRow);

        _startup.Margin = new Padding(0, 12, 0, 0);
        p.Controls.Add(_startup);
        return Wrap("Basics", p);
    }

    private TabPage WherePage()
    {
        var p = Page();
        p.Controls.Add(Head("What stays lit", 4));
        _follow.Margin = new Padding(0, 2, 0, 2);
        p.Controls.Add(_follow);
        var areaRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _area.Margin = new Padding(0, 6, 8, 0);
        areaRow.Controls.Add(_area);
        areaRow.Controls.Add(_pickArea);
        p.Controls.Add(areaRow);
        _areaNote.Margin = new Padding(20, 2, 0, 0);
        p.Controls.Add(_areaNote);

        p.Controls.Add(Head("Screens", 14));
        p.Controls.Add(_oneScreen);
        p.Controls.Add(Note("With more than one screen, the screens that do not have the light stay normal."));

        p.Controls.Add(Head("Always lit", 14));
        p.Controls.Add(_shell);
        p.Controls.Add(Note("The taskbar, the ^ tray pop-up, jump lists and right-click menus stay bright so you can always find things."));

        p.Controls.Add(Head("Edge", 14));
        p.Controls.Add(_soft);
        p.Controls.Add(Note("The dimming fades in over a few pixels instead of a hard edge."));
        return Wrap("Where", p);
    }

    private TabPage AppsPage()
    {
        var p = Page();
        p.Controls.Add(Head("Never dim around these programs", 4));
        p.Controls.Add(new Label { Text = "While one of them is the window you are in, nothing is dimmed.", AutoSize = true, ForeColor = Muted, MaximumSize = new Size(370, 0), Margin = new Padding(0, 0, 0, 4) });
        _appsHint.Text = "No programs yet. Open the program, pick it in the list below (or type its name, like msedge) and press Add.";
        p.Controls.Add(_appsHint);
        _apps.Margin = new Padding(0, 0, 0, 4);
        p.Controls.Add(_apps);
        var add = Row();
        add.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
        _running.Margin = new Padding(0, 2, 8, 2);
        add.Controls.Add(_running, 0, 0);
        add.Controls.Add(_addApp, 1, 0);
        p.Controls.Add(add);
        _removeApp.Margin = new Padding(0, 2, 0, 0);
        p.Controls.Add(_removeApp);

        return Wrap("Apps", p);
    }

    private TabPage FocusPage()
    {
        var p = Page();
        p.Controls.Add(Head("Notifications", 4));
        p.Controls.Add(_notify);
        p.Controls.Add(Note("Windows notification pop-ups stay bright while the rest is dimmed. Not yet verified on a real PC: tell us if one stays dim."));

        p.Controls.Add(Head("Meetings", 10));
        p.Controls.Add(_meetings);
        p.Controls.Add(Note("Zoom, Teams, Webex, Skype: nothing is dimmed while one is in front."));

        p.Controls.Add(Head("Screen sharing", 10));
        p.Controls.Add(_hideCapture);
        p.Controls.Add(Note("Others on your shared screen see no dimming."));
        return Wrap("Focus", p);
    }

    private static TableLayoutPanel Row()
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    /// <summary>
    /// The tabs are as tall as their tallest page needs, at whatever display scale this screen has (a fixed height cut the last
    /// option off at 125%, seen in the Sandbox). If the screen is too short for that, the pages scroll instead.
    /// </summary>
    private void FitTabs()
    {
        int need = 0;
        foreach (TabPage p in _tabs.TabPages)
            if (p.Controls.Count > 0)
                need = Math.Max(need, p.Controls[0].GetPreferredSize(new Size(Math.Max(1, p.ClientSize.Width - p.Padding.Horizontal), 0)).Height + p.Padding.Vertical);
        int header = _tabs.Height - _tabs.DisplayRectangle.Height;
        int want = need + header + 12;
        int avail = Screen.FromControl(this).WorkingArea.Height - (Height - _tabs.Height) - 24;
        _tabs.Height = Math.Max(180, Math.Min(want, avail));
        PerformLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitTabs();
        if (_near is { } n)
        {
            var wa = Screen.FromRectangle(new Rectangle(n.Left, n.Top, n.Width, n.Height)).WorkingArea;
            var (x, y) = SettingsPlacement.Below(n, new Box(wa.Left, wa.Top, wa.Right, wa.Bottom), Width, Height, 10);
            Location = new Point(x, y); // right under the pill that was clicked
        }
        else CenterToScreen();
    }

    // ---- the look (follows the quick bar's theme) ----

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetTitleBar(); }

    private void SetTitleBar()
    {
        if (!IsHandleCreated) return;
        int dark = _c.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, 4);
    }

    private void ApplyTheme(bool dark)
    {
        _c = dark ? DarkC : LightC; _appliedTheme = dark ? "Dark" : "Light";
        BackColor = _c.Back; ForeColor = _c.Text;
        SetTitleBar();
        Walk(this);
    }

    private void Walk(Control c)
    {
        switch (c)
        {
            case Label l when l == _status: break; // its color says on, off or paused (set in Reload)
            case Label l:
                bool muted = l.Tag as string == "muted" || l.ForeColor.ToArgb() == LightC.Muted.ToArgb() || l.ForeColor.ToArgb() == DarkC.Muted.ToArgb();
                if (muted) l.Tag = "muted";
                l.ForeColor = muted ? _c.Muted : _c.Text; break;
            case Button b:
                b.FlatStyle = _c.Dark ? FlatStyle.Flat : FlatStyle.Standard; b.UseVisualStyleBackColor = !_c.Dark;
                b.FlatAppearance.BorderColor = _c.Border; b.FlatAppearance.MouseOverBackColor = _c.Dark ? Color.FromArgb(62, 65, 73) : SystemColors.ControlLight;
                b.BackColor = _c.Dark ? _c.Control : SystemColors.Control; b.ForeColor = _c.Text; break;
            case CheckBox or RadioButton: c.ForeColor = _c.Text; c.BackColor = Color.Transparent; break;
            case TextBox t: t.BackColor = _c.Control; t.ForeColor = _c.Text; break;
            case ListBox lb: lb.BackColor = _c.Control; lb.ForeColor = _c.Text; lb.BorderStyle = BorderStyle.FixedSingle; Native.SetWindowTheme(lb.Handle, _c.Dark ? "DarkMode_Explorer" : "Explorer", null); break;
            case ComboBox cb: cb.FlatStyle = _c.Dark ? FlatStyle.Flat : FlatStyle.Standard; cb.BackColor = _c.Control; cb.ForeColor = _c.Text; Native.SetWindowTheme(cb.Handle, _c.Dark ? "DarkMode_CFD" : "Explorer", null); break;
            case TrackBar tb: tb.BackColor = _c.Page; break;
            case ThemedTabs tc: tc.BackColor = _c.Back; tc.Theme(_c); break;
            case TabPage tp: tp.UseVisualStyleBackColor = false; tp.BackColor = _c.Page; Native.SetWindowTheme(tp.Handle, _c.Dark ? "DarkMode_Explorer" : "Explorer", null); break;
        }
        foreach (Control k in c.Controls) Walk(k);
    }

    // ---- reading and saving --------------------------------------------------------------------

    private static DateTime Stamp() { try { return File.GetLastWriteTimeUtc(Settings.FilePath); } catch { return default; } }

    private void Reload()
    {
        _view = Settings.Load();
        _fileStamp = Stamp();
        if (_view.QuickBarTheme != _appliedTheme) ApplyTheme(_view.QuickBarTheme == "Dark");
        var now = DateTime.UtcNow;
        bool paused = _view.Enabled && _view.IsPaused(now);
        _lastPaused = paused;
        _loading = true;
        try
        {
            _status.Text = !_view.Enabled ? "Spotlight is off" : paused ? $"Spotlight is paused until {_view.PausedUntilUtc!.Value.ToLocalTime():t}" : "Spotlight is on";
            _status.ForeColor = !_view.Enabled || paused ? _c.Muted : _c.Good;
            _toggle.Text = !_view.Enabled ? "Turn on" : paused ? "Resume now" : "Turn off";
            if (!_slider.Capture && _slider.Value != _view.DimPercent) _slider.Value = _view.DimPercent;
            UpdatePercentLabel();
            int tint = Tints.IndexOf(_view.Tint);
            for (int i = 0; i < _swatches.Count; i++) { _swatches[i].Chosen = i == tint; _swatches[i].Invalidate(); }
            if (!_capturing) _hotkeyBox.Text = _view.ParsedHotkey.ToString();
            _pause.Enabled = _view.Enabled;
            _pause.Text = paused ? "Resume now" : "Pause for 1 hour";
            _pauseNote.Text = paused ? "Back on automatically at " + _view.PausedUntilUtc!.Value.ToLocalTime().ToString("t") : "";
            _startup.Checked = Settings.StartsAtSignIn();
            _barOn.Checked = _view.QuickBar;
            _themeDark.Checked = _view.QuickBarTheme != "Light"; _themeLight.Checked = _view.QuickBarTheme == "Light";
            _themeDark.Enabled = _themeLight.Enabled = _view.QuickBar;
            _follow.Checked = !_view.AreaMode;
            _area.Checked = _view.AreaMode;
            _pickArea.Text = _view.HasArea ? "Pick a new area..." : "Pick area...";
            _areaNote.Text = _view.AreaBox is { } a ? $"Area: {a.Width} x {a.Height} pixels at {a.Left}, {a.Top}. The light stays there whichever window you use." : "No area picked yet.";
            _appsHint.Visible = _view.NeverDimApps.Count == 0;
            if (_view.AreaMode && _view.AreaBox is { } ab && Geometry.UsableArea(ab, Win.Monitors()) is null) _areaNote.Text = "That area is no longer on any screen (it was on a screen that is gone). Pick a new one.";
            _oneScreen.Checked = _view.OneScreenOnly;
            _soft.Checked = _view.SoftEdge;
            _shell.Checked = _view.KeepShellBright;
            _notify.Checked = _view.LightNotifications;
            _meetings.Checked = _view.SkipMeetingApps;
            _hideCapture.Checked = _view.HideFromCapture;
            if (!_apps.Items.Cast<string>().SequenceEqual(_view.NeverDimApps))
            {
                _apps.Items.Clear();
                foreach (var n in _view.NeverDimApps) _apps.Items.Add(n);
            }
        }
        finally { _loading = false; }
    }

    private void UpdatePercentLabel() => _percent.Text = _slider.Value + "%";

    /// <summary>Applies one change on top of the newest saved settings and tells the tray copy (see Settings.Change).</summary>
    private void Persist(Action<Settings> change)
    {
        bool saved = Settings.Change(change);
        Reload();
        if (!saved && !Instance.IsTest && Settings.TakeProblem() is { } problem)
            MessageBox.Show(this, problem, "Spotlight", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void FlushSlider()
    {
        _saveSoon.Stop();
        int v = _slider.Value;
        if (v != _view.DimPercent) Persist(s => s.DimPercent = v);
    }

    private void Watch()
    {
        // The tray copy is gone (Quit, or the installer asked it to leave): close too, so Spotlight.exe is never left locked.
        if (Mutex.TryOpenExisting(Instance.MutexName, out var m)) m.Dispose();
        else { Close(); return; }
        if (_slider.Capture || !Visible) return;
        bool paused = _view.Enabled && _view.IsPaused(DateTime.UtcNow);
        if (Stamp() != _fileStamp || paused != _lastPaused || Settings.StartsAtSignIn() != _startup.Checked) Reload();
    }

    // ---- the area picker -----------------------------------------------------------------------

    private void DoPick()
    {
        Hide(); // out of the way while the sheet is up
        Box? picked;
        try { picked = AreaPicker.Pick(); }
        finally { Show(); Activate(); }
        if (picked is { } b)
            Persist(s => { s.Area = new[] { b.Left, b.Top, b.Right, b.Bottom }; s.AreaMode = true; s.Enabled = true; s.PausedUntilUtc = null; });
        else Reload(); // canceled: put the choice back to what is saved
    }

    // ---- never-dim programs --------------------------------------------------------------------

    private void FillRunning()
    {
        var names = new SortedSet<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0 && p.Id != Environment.ProcessId && Rules.NormalizeApp(p.ProcessName) != "spotlight") names.Add(Rules.NormalizeApp(p.ProcessName)); }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        _running.Items.Clear();
        foreach (var n in names) _running.Items.Add(n);
    }

    private void AddApp()
    {
        string name = Rules.NormalizeApp(Path.GetFileName(_running.Text.Trim().Replace('/', (char)92)));
        bool ok = name.Length is > 0 and <= 60 && name.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ' or '+') && name != "spotlight";
        if (!ok) { _appsHint.Text = "Use a program name like msedge or zoom (letters and numbers), not a path."; _appsHint.Visible = true; return; }
        _running.Text = "";
        Persist(s => { if (!s.NeverDimApps.Contains(name)) s.NeverDimApps.Add(name); });
    }

    private void RemoveApp()
    {
        if (_apps.SelectedItem is not string name) return;
        Persist(s => s.NeverDimApps.Remove(name));
    }

    // ---- changing the shortcut -----------------------------------------------------------------

    private void BeginCapture()
    {
        _capturing = true;
        _change.Text = "Cancel";
        _hotkeyBox.Text = "Press the new shortcut";
        ShowNote("Hold Ctrl or Alt and press a letter, number or F-key. Esc cancels.", false);
    }

    private void EndCapture()
    {
        _capturing = false;
        _change.Text = "Change";
        _hotkeyNote.Visible = false;
        _hotkeyBox.Text = _view.ParsedHotkey.ToString();
    }

    private void ShowNote(string text, bool problem)
    {
        _hotkeyNote.Text = text;
        _hotkeyNote.ForeColor = problem ? _c.Bad : _c.Muted;
        _hotkeyNote.Visible = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);
        Keys key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { EndCapture(); return true; }
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true; // only a modifier so far: keep waiting
        bool ctrl = (keyData & Keys.Control) != 0, alt = (keyData & Keys.Alt) != 0, shift = (keyData & Keys.Shift) != 0;
        if (!Hotkey.TryFromKeys(ctrl, alt, shift, false, (uint)key, out var hk))
        {
            ShowNote("That will not work. Hold Ctrl or Alt and press a letter, number or F-key.", true);
            return true;
        }
        if (hk != _view.ParsedHotkey && !CanUse(hk))
        {
            ShowNote($"{hk} is already used by another program. Try a different one.", true);
            return true;
        }
        string text = hk.ToString();
        EndCapture();
        Persist(s => s.Hotkey = text);
        return true;
    }

    /// <summary>Tries the shortcut for a moment, to find out whether Windows lets Spotlight have it.</summary>
    private bool CanUse(Hotkey hk)
    {
        if (!Native.RegisterHotKey(Handle, TestHotkeyId, hk.Modifiers | 0x4000, hk.VirtualKey)) return false;
        Native.UnregisterHotKey(Handle, TestHotkeyId);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _watch.Dispose(); _saveSoon.Dispose(); }
        base.Dispose(disposing);
    }
}
