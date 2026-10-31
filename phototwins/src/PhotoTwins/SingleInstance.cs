namespace PhotoTwins;

/// <summary>
/// Only one copy of the app runs at a time (two copies would fight over the same saved settings and memory of scans).
/// A second double-click doesn't open a second window: it signals the first copy, which comes to the front.
/// The installer uses <see cref="RequestQuit"/> to ask the running copy to close itself (stopping and saving first) before
/// a new version is swapped in, so there is only ever one copy.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _show;
    private readonly EventWaitHandle? _quit;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread? _listener;

    public bool IsFirst { get; }

    private static string ShowName(string name) => $@"Local\{name}.Show";
    private static string QuitName(string name) => $@"Local\{name}.Quit";

    public SingleInstance(string name, Action onOtherCopyStarted, Action? onQuitRequested = null)
    {
        _mutex = new Mutex(true, $@"Local\{name}.Instance", out bool first);
        IsFirst = first;
        if (first)
        {
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName(name));
            _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName(name));
            _listener = new Thread(() =>
            {
                while (true)
                {
                    int which = WaitHandle.WaitAny(new WaitHandle[] { _show, _quit, _stop });
                    if (which == 2) return;
                    try { if (which == 0) onOtherCopyStarted(); else onQuitRequested?.Invoke(); } catch { /* bringing the window forward or closing it is best effort */ }
                }
            }) { IsBackground = true, Name = "PhotoTwins second-copy listener" };
            _listener.Start();
        }
        else
        {
            try { EventWaitHandle.OpenExisting(ShowName(name)).Set(); } catch { /* the first copy is there anyway */ }
        }
    }

    /// <summary>Asks a running copy to close itself. Returns false if none is running.</summary>
    public static bool RequestQuit(string name)
    {
        try { using var e = EventWaitHandle.OpenExisting(QuitName(name)); e.Set(); return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        _stop.Set();
        _listener?.Join(2000);
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch { /* already released */ } }
        _mutex.Dispose();
        _show?.Dispose();
        _quit?.Dispose();
        _stop.Dispose();
    }
}
