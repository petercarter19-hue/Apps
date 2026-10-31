using System.Diagnostics;
using System.Text;

namespace Glance;

/// <summary>
/// Everything ProcessKiller asks Windows, in one small seam. The real one (<see cref="WinProcApi"/>) talks to Windows.
/// Tests swap in a fake, so the safety rules can be checked with made-up processes and nothing real is ever ended.
/// </summary>
internal interface IProcApi
{
    uint CurrentSession { get; }
    /// <summary>The account Glance is running as (the elevated helper runs as the same account), or null if Windows won't say.</summary>
    string? CurrentSid { get; }

    /// <summary>Opens a process to end it. Returns Zero if it can't; <paramref name="accessDenied"/> says whether Windows refused (as opposed to the process being gone).</summary>
    IntPtr Open(int pid, out bool accessDenied);
    bool GetCreated(IntPtr process, out long created);
    /// <summary>The exe name without folder or extension, or "" if unknown.</summary>
    string ExeName(IntPtr process);
    /// <summary>The account that owns this exact process, read from the same open handle; null if unknown.</summary>
    string? OwnerSid(IntPtr process);
    bool SessionOf(int pid, out uint session);
    bool Terminate(IntPtr process);
    void Close(IntPtr process);

    /// <summary>Is this user's Explorer running in this session? Unknown owners count as running, so a second shell isn't started by mistake.</summary>
    bool ExplorerRunningFor(string? sid);
    void StartExplorer();
    void Sleep(int milliseconds);
}

internal sealed class WinProcApi : IProcApi
{
    private uint? _session;
    private string? _sid;
    private bool _sidRead;
    private readonly StringBuilder _path = new(1024);

    public uint CurrentSession => _session ??= (uint)Process.GetCurrentProcess().SessionId;

    public string? CurrentSid
    {
        get
        {
            if (!_sidRead) { _sid = Native.OwnerSid(Native.GetCurrentProcess()); _sidRead = true; }
            return _sid;
        }
    }

    public IntPtr Open(int pid, out bool accessDenied)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        accessDenied = h == IntPtr.Zero && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 5;
        return h;
    }

    public bool GetCreated(IntPtr process, out long created) => Native.GetProcessTimes(process, out created, out _, out _, out _);

    public string ExeName(IntPtr process)
    {
        lock (_path)
        {
            int cap = _path.Capacity;
            _path.Clear();
            return Native.QueryFullProcessImageName(process, 0, _path, ref cap) ? Path.GetFileNameWithoutExtension(_path.ToString()) : "";
        }
    }

    public string? OwnerSid(IntPtr process) => Native.OwnerSid(process);
    public bool SessionOf(int pid, out uint session) => Native.ProcessIdToSessionId(pid, out session);
    public bool Terminate(IntPtr process) => Native.TerminateProcess(process, 1);
    public void Close(IntPtr process) => Native.CloseHandle(process);

    public bool ExplorerRunningFor(string? sid)
    {
        Process[] running = Process.GetProcessesByName("explorer");
        try
        {
            foreach (var p in running)
            {
                if (p.SessionId != CurrentSession) continue;
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) return true; // can't tell whose it is: assume it's the user's
                try
                {
                    string? owner = Native.OwnerSid(h);
                    if (owner == null || ProcessRules.SameSid(owner, sid)) return true;
                }
                finally { Native.CloseHandle(h); }
            }
            return false;
        }
        finally { foreach (var p in running) p.Dispose(); }
    }

    public void StartExplorer()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
    }

    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
}
