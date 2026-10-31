using System.Runtime.InteropServices;

namespace Glance;

/// <summary>Hidden top-level window that receives tray callbacks and the TaskbarCreated broadcast.</summary>
internal sealed class TrayWindow : NativeWindow
{
    public static readonly int TaskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly Dictionary<int, TrayIcon> _icons = new();

    public event Action? TaskbarCreated;

    public TrayWindow()
    {
        // A real (hidden) top-level window, not a message-only one: HWND_BROADCAST messages such as
        // TaskbarCreated only reach top-level windows, and we need that to survive an Explorer restart.
        CreateHandle(new CreateParams { Caption = Instance.TrayTitle, ExStyle = 0x80 });
    }

    internal void Register(TrayIcon icon, int id) => _icons[id] = icon;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_TRAYCALLBACK)
        {
            long lp = m.LParam.ToInt64();
            long wp = m.WParam.ToInt64();
            int evt = (int)(lp & 0xFFFF);
            int id = (int)((lp >> 16) & 0xFFFF);
            var at = new Point((short)(wp & 0xFFFF), (short)((wp >> 16) & 0xFFFF));
            if (_icons.TryGetValue(id, out var icon)) icon.Handle(evt, at);
            return;
        }
        if (m.Msg == TaskbarCreatedMsg)
        {
            TaskbarCreated?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>One notification-area icon using Shell_NotifyIcon version 4, so the shell tells us when to pop our own hover UI.</summary>
internal sealed class TrayIcon
{
    private readonly TrayWindow _window;
    private readonly int _id;
    private IntPtr _hIcon;
    private string _tip = "";
    private bool _added;

    public event Action? HoverOpen;
    public event Action? HoverClose;
    public event Action? MouseMoved;
    public event Action? Click;
    public event Action<Point>? ContextMenu;

    public bool Visible => _added;

    public TrayIcon(TrayWindow window, int id)
    {
        _window = window;
        _id = id;
        window.Register(this, id);
    }

    private Native.NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = _id,
        uFlags = flags,
        uCallbackMessage = Native.WM_TRAYCALLBACK,
        hIcon = _hIcon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Takes ownership of the icon handle and destroys the previous one once the shell has copied the new one.</summary>
    public void SetIcon(IntPtr hIcon, string tip)
    {
        IntPtr old = _hIcon;
        _hIcon = hIcon;
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (!_added) Add();
        else
        {
            var d = Data(Native.NIF_ICON | Native.NIF_TIP);
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
        }
        if (old != IntPtr.Zero && old != hIcon) Native.DestroyIcon(old);
    }

    private void Add()
    {
        if (_hIcon == IntPtr.Zero) return;
        // No NIF_SHOWTIP: with version 4 that suppresses the stock tooltip and makes the shell send
        // NIN_POPUPOPEN / NIN_POPUPCLOSE so we can show the card instead.
        var d = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP);
        if (!Native.Shell_NotifyIcon(Native.NIM_ADD, ref d)) return;
        _added = true;
        var v = Data(0);
        v.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref v);
    }

    /// <summary>A Windows notification from this icon (shown even while the icon sits under the ^ arrow).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var d = Data(Native.NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = Native.NIIF_INFO;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
    }

    public void Remove()
    {
        if (!_added) return;
        var d = Data(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d);
        _added = false;
    }

    /// <summary>After Explorer restarts the old registration is gone; add it again.</summary>
    public void ReAdd()
    {
        _added = false;
        Add();
    }

    public void Dispose()
    {
        Remove();
        if (_hIcon != IntPtr.Zero) { Native.DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }

    /// <summary>Screen rectangle of the icon in physical pixels, or empty if the shell can't tell us.</summary>
    public Rectangle GetRect()
    {
        if (!_added) return Rectangle.Empty;
        var id = new Native.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = _window.Handle, uID = _id };
        if (Native.Shell_NotifyIconGetRect(ref id, out var r) != 0) return Rectangle.Empty;
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    internal void Handle(int evt, Point at)
    {
        switch (evt)
        {
            case Native.WM_MOUSEMOVE: MouseMoved?.Invoke(); break;
            case Native.NIN_POPUPOPEN: HoverOpen?.Invoke(); break;
            case Native.NIN_POPUPCLOSE: HoverClose?.Invoke(); break;
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT: Click?.Invoke(); break;
            case Native.WM_CONTEXTMENU: ContextMenu?.Invoke(at); break;
        }
    }
}
