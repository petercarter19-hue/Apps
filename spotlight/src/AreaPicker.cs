namespace Spotlight;

/// <summary>
/// "Pick an area", like a snipping tool: a see-through sheet over every screen, drag a rectangle, let go. Spotlight then dims
/// everything except that area. Esc (or a right-click) cancels. Runs in the settings window's copy of Spotlight.exe, never in the tray copy.
/// </summary>
internal sealed class AreaPicker : Form
{
    private const int MinSize = 60;
    private readonly Box _screens;
    private readonly Box _primary;
    private Point _start, _now;
    private bool _dragging;

    /// <summary>The chosen area in screen pixels, or null if canceled.</summary>
    public Box? Result { get; private set; }

    public static Box? Pick()
    {
        using var f = new AreaPicker();
        f.ShowDialog();
        return f.Result;
    }

    /// <summary>The whole job for the tray menu: pick, save it as the area to light, and tell the tray copy.</summary>
    public static void PickAndSave()
    {
        using var held = new Mutex(true, Instance.PickerMutexName, out _); // visible to the installer from the sheet until after the save
        if (Pick() is not { } b) return;
        if (Mutex.TryOpenExisting(Instance.MutexName, out var alive)) alive.Dispose(); else return; // Spotlight quit while the sheet was up: save nothing
        bool saved = Settings.Change(s =>
        {
            s.Area = new[] { b.Left, b.Top, b.Right, b.Bottom };
            s.AreaMode = true;
            s.Enabled = true;
            s.PausedUntilUtc = null;
        });
        if (!saved && !Instance.IsTest && Settings.TakeProblem() is { } problem)
            MessageBox.Show(problem, "Spotlight", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private AreaPicker()
    {
        var monitors = Win.Monitors();
        _screens = monitors.Count == 0 ? new Box(0, 0, 1024, 768) : new Box(monitors.Min(m => m.Bounds.Left), monitors.Min(m => m.Bounds.Top), monitors.Max(m => m.Bounds.Right), monitors.Max(m => m.Bounds.Bottom));
        _primary = monitors.Count == 0 ? _screens : monitors[0].Bounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.45;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Spotlight area";
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        // The tray copy is gone (Quit, uninstall, upgrade): this sheet goes too, so Spotlight.exe is never left running or locked.
        var watch = new System.Windows.Forms.Timer { Interval = 300 };
        watch.Tick += (_, _) => { if (Mutex.TryOpenExisting(Instance.MutexName, out var m)) m.Dispose(); else Close(); };
        watch.Start();
        FormClosed += (_, _) => watch.Dispose();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _screens.Left, _screens.Top, _screens.Width, _screens.Height - 1, Native.SWP_SHOWWINDOW); // one row short of full screen (see Geometry.OverlayBounds)
        Activate();
    }

    // A move between screens with different scaling must not resize this sheet.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x02E0 /* WM_DPICHANGED */) return;
        base.WndProc(ref m);
    }

    private Rectangle Selection()
    {
        int x1 = Math.Min(_start.X, _now.X), y1 = Math.Min(_start.Y, _now.Y);
        return new Rectangle(x1, y1, Math.Abs(_now.X - _start.X), Math.Abs(_now.Y - _start.Y));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        _start = _now = e.Location; _dragging = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _now = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        _now = e.Location;
        var r = Selection();
        if (r.Width < MinSize || r.Height < MinSize) { Invalidate(); return; } // too small: keep waiting for a real drag
        Result = new Box(_screens.Left + r.Left, _screens.Top + r.Top, _screens.Left + r.Right, _screens.Top + r.Bottom);
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var font = new Font("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Point);
        const string hint = "Drag to choose the area to keep lit.   Esc to cancel.";
        var size = g.MeasureString(hint, font);
        float hx = _primary.Left - _screens.Left + (_primary.Width - size.Width) / 2, hy = _primary.Top - _screens.Top + _primary.Height * 0.2f; // low enough to sit below the floating quick bar
        g.DrawString(hint, font, Brushes.White, hx, hy);
        if (!_dragging) return;
        var r = Selection();
        using var fill = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
        using var pen = new Pen(Color.White, 2f);
        g.FillRectangle(fill, r);
        g.DrawRectangle(pen, r);
        string label = $"{r.Width} x {r.Height}" + (r.Width < MinSize || r.Height < MinSize ? "  (too small)" : "");
        g.DrawString(label, font, Brushes.White, r.Left + 6, Math.Max(0, r.Top - 34));
    }
}
