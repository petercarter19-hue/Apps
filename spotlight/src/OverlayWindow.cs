namespace Spotlight;

/// <summary>
/// One dark, see-through, click-through window covering one monitor. The "light" is a hole cut in its shape (a window region),
/// so moving the light costs almost nothing: no bitmap is redrawn, only the region changes. It never takes focus.
/// </summary>
internal sealed class OverlayWindow : NativeWindow, IDisposable
{
    private IntPtr _brush = Native.CreateSolidBrush(0x000000);
    private int _color;
    private Box _bounds;
    private bool _shown;
    private byte _alpha = 255;
    private IReadOnlyList<MonitorHole> _holes = Array.Empty<MonitorHole>();
    private bool _regionSet;

    public OverlayWindow(Box bounds)
    {
        _bounds = bounds;
        CreateHandle(new CreateParams
        {
            Caption = "Spotlight overlay",
            Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
            X = bounds.Left, Y = bounds.Top, Width = bounds.Width, Height = bounds.Height,
        });
        Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA);
    }

    public bool Shown => _shown;

    /// <summary>The color of the dimming (GDI color value).</summary>
    public void SetColor(int colorRef)
    {
        if (colorRef == _color) return;
        _color = colorRef;
        var old = _brush;
        _brush = Native.CreateSolidBrush(colorRef);
        Native.DeleteObject(old);
        Native.InvalidateRect(Handle, IntPtr.Zero, true);
    }

    /// <summary>True = the dimming is left out of screen sharing and recordings (people watching your screen see no dimming).</summary>
    public void SetHiddenFromCapture(bool hidden) => Native.SetWindowDisplayAffinity(Handle, hidden ? 0x11u : 0u);

    public void SetAlpha(byte alpha)
    {
        if (alpha == _alpha && _shown) return;
        _alpha = alpha;
        Native.SetLayeredWindowAttributes(Handle, 0, alpha, Native.LWA_ALPHA);
    }

    /// <summary>Cuts the lit areas out of the dimming (the lit window, and notification pop-ups). None = all of this monitor is dimmed.</summary>
    public void SetHoles(IReadOnlyList<MonitorHole> holes)
    {
        if (_regionSet && _holes.SequenceEqual(holes)) return;
        _holes = holes.ToArray(); _regionSet = true;
        if (holes.Count == 0) { Native.SetWindowRgn(Handle, IntPtr.Zero, false); return; }

        IntPtr full = Native.CreateRectRgn(0, 0, _bounds.Width, _bounds.Height);
        foreach (var h in holes)
        {
            IntPtr cut = h.Radius > 0
                ? Native.CreateRoundRectRgn(h.Local.Left, h.Local.Top, h.Local.Right + 1, h.Local.Bottom + 1, h.Radius * 2, h.Radius * 2)
                : Native.CreateRectRgn(h.Local.Left, h.Local.Top, h.Local.Right, h.Local.Bottom);
            Native.CombineRgn(full, full, cut, Native.RGN_DIFF);
            Native.DeleteObject(cut);
        }
        Native.SetWindowRgn(Handle, full, false); // Windows owns "full" from here
    }

    public void Show()
    {
        if (_shown) return;
        _shown = true;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    /// <summary>Keeps the dimming above other always-on-top windows.</summary>
    public void RaiseToTop()
    {
        if (_shown) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_NCHITTEST: m.Result = (IntPtr)Native.HTTRANSPARENT; return;
            case Native.WM_MOUSEACTIVATE: m.Result = (IntPtr)Native.MA_NOACTIVATE; return;
            case Native.WM_ERASEBKGND:
                if (Native.GetClientRect(Handle, out var rc)) Native.FillRect(m.WParam, ref rc, _brush);
                m.Result = (IntPtr)1;
                return;
            case Native.WM_PAINT:
                var hdc = Native.BeginPaint(Handle, out var ps);
                var area = ps.rcPaint;
                Native.FillRect(hdc, ref area, _brush);
                Native.EndPaint(Handle, ref ps);
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
        Native.DeleteObject(_brush);
    }
}
