using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

internal enum SortMode { Memory, Cpu }

/// <summary>What End needs to know about one process: its parent, when it started, which sign-in session it's in, and its exe name.</summary>
internal readonly record struct ProcInfo(int Parent, long Created, uint Session, string Exe);

/// <summary>All processes that share one executable, the way Task Manager rolls Chrome's forty processes into one row.</summary>
internal sealed class ProcessGroup
{
    public string Key = "";
    public string Name = "";
    public string? ExePath;
    public List<int> Pids = new();
    public long Memory;
    public double Cpu;
    public Bitmap? Icon;
    public bool Protected;
    public bool IsExplorer;
    public bool IsSelf;

    public string Initials
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            string s = parts[0][..1];
            if (parts.Length > 1) s += parts[1][..1];
            return s.ToUpperInvariant();
        }
    }
}

internal sealed class ProcessScanner
{
    // Windows pieces that must never get an End button. Everything else is fair game and simply respawns if Windows needs it.
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "memcompression", "registry", "secure system", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "msmpeng", "audiodg", "sihost",
        "taskhostw", "ctfmon", "ntoskrnl",
    };

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = "System",
        ["memcompression"] = "Memory Compression",
        ["registry"] = "Registry",
        ["secure system"] = "Secure System",
        ["svchost"] = "Windows services",
        ["dwm"] = "Desktop Window Manager",
        ["msmpeng"] = "Windows Defender",
    };

    private const long GrowerThreshold = 100L * 1024 * 1024;
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HistoryStep = TimeSpan.FromSeconds(10);

    private Dictionary<(int pid, long created), long> _lastCpu = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime at, Dictionary<string, long> mem)> _history = new();
    private readonly StringBuilder _pathBuf = new(1024);
    private readonly int _cores = Environment.ProcessorCount;
    private readonly int _selfPid = Environment.ProcessId;
    private long _lastStamp;

    /// <summary>Every process from the latest scan, used to end a whole app tree safely.</summary>
    public Dictionary<int, ProcInfo> Procs { get; private set; } = new();

    public static bool IsProtectedName(string exeNoExt) => ProtectedNames.Contains(exeNoExt);

    public List<ProcessGroup> Scan(int topN, SortMode sort)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastStamp == 0 ? 0 : (now - _lastStamp) / (double)Stopwatch.Frequency;
        _lastStamp = now;

        var groups = new Dictionary<string, ProcessGroup>(StringComparer.OrdinalIgnoreCase);
        var procs = new Dictionary<int, ProcInfo>();
        var nextCpu = new Dictionary<(int, long), long>();

        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return new List<ProcessGroup>();
        try
        {
            var e = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (Native.Process32FirstW(snap, ref e))
            {
                do
                {
                    int pid = (int)e.th32ProcessID;
                    if (pid == 0) continue;

                    string exe = e.szExeFile;
                    string? path = null;
                    long mem = 0;
                    double cpu = 0;
                    long created = 0;

                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            mem = ReadPrivateMemory(h);
                            if (Native.GetProcessTimes(h, out long c, out _, out long kernel, out long user))
                            {
                                created = c;
                                long t = kernel + user;
                                var key = (pid, created);
                                nextCpu[key] = t;
                                if (elapsed > 0 && _lastCpu.TryGetValue(key, out long prev))
                                    cpu = Math.Max(0, (t - prev) / (elapsed * 1e7) / _cores * 100.0);
                            }
                            int cap = _pathBuf.Capacity;
                            _pathBuf.Clear();
                            if (Native.QueryFullProcessImageName(h, 0, _pathBuf, ref cap)) path = _pathBuf.ToString();
                        }
                        finally { Native.CloseHandle(h); }
                    }

                    uint session = Native.ProcessIdToSessionId(pid, out uint sid) ? sid : uint.MaxValue;
                    procs[pid] = new ProcInfo((int)e.th32ParentProcessID, created, session, Path.GetFileNameWithoutExtension(exe));

                    string gkey = (path ?? exe).ToLowerInvariant();
                    if (!groups.TryGetValue(gkey, out var g))
                    {
                        g = new ProcessGroup { Key = gkey, ExePath = path, Name = exe };
                        groups[gkey] = g;
                    }
                    g.Pids.Add(pid);
                    g.Memory += mem;
                    g.Cpu += cpu;
                }
                while (Native.Process32NextW(snap, ref e));
            }
        }
        finally { Native.CloseHandle(snap); }

        _lastCpu = nextCpu;
        Procs = procs;
        RecordHistory(groups);

        IEnumerable<ProcessGroup> ordered = sort == SortMode.Cpu
            ? groups.Values.OrderByDescending(g => g.Cpu).ThenByDescending(g => g.Memory)
            : groups.Values.OrderByDescending(g => g.Memory).ThenByDescending(g => g.Cpu);
        var top = ordered.Take(topN).ToList();
        foreach (var g in top) Decorate(g);
        return top;
    }

    private static long ReadPrivateMemory(IntPtr h)
    {
        uint cb2 = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>();
        if (Native.GetProcessMemoryInfo(h, out var c, cb2))
        {
            // Private working set is the "Memory" column Task Manager shows. Older Windows leaves it zero; fall back to commit.
            ulong pws = (ulong)c.PrivateWorkingSetSize;
            return (long)(pws != 0 ? pws : (ulong)c.PrivateUsage);
        }
        uint cbEx = cb2 - 8 - (uint)IntPtr.Size;
        if (Native.GetProcessMemoryInfo(h, out c, cbEx)) return (long)(ulong)c.PrivateUsage;
        return 0;
    }

    private void Decorate(ProcessGroup g)
    {
        string exeNoExt = Path.GetFileNameWithoutExtension(g.ExePath ?? g.Name);
        g.IsSelf = g.Pids.Contains(_selfPid);
        g.IsExplorer = exeNoExt.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        g.Protected = !g.IsSelf && (g.ExePath == null || ProtectedNames.Contains(exeNoExt));

        if (g.IsSelf) g.Name = "Glance";
        else if (FriendlyNames.TryGetValue(exeNoExt, out var friendly)) g.Name = friendly;
        else g.Name = ResolveName(g.ExePath, exeNoExt);

        g.Icon = g.ExePath == null ? null : ResolveIcon(g.ExePath);
    }

    private string ResolveName(string? path, string fallback)
    {
        if (path == null) return fallback;
        if (_names.TryGetValue(path, out var cached)) return cached;
        string name = fallback;
        try
        {
            string? desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc) && desc.Length >= 2) name = desc;
        }
        catch { }
        _names[path] = name;
        return name;
    }

    private Bitmap? ResolveIcon(string path)
    {
        if (_icons.TryGetValue(path, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            var info = new Native.SHFILEINFO();
            if (Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), Native.SHGFI_ICON | Native.SHGFI_LARGEICON) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
                bmp = icon.ToBitmap();
                Native.DestroyIcon(info.hIcon);
            }
        }
        catch { }
        _icons[path] = bmp;
        return bmp;
    }

    private void RecordHistory(Dictionary<string, ProcessGroup> groups)
    {
        var now = DateTime.UtcNow;
        if (_history.Count > 0 && now - _history[^1].at < HistoryStep) return;
        var snapshot = new Dictionary<string, long>(groups.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in groups) snapshot[kv.Key] = kv.Value.Memory;
        _history.Add((now, snapshot));
        while (_history.Count > 0 && now - _history[0].at > HistoryWindow + HistoryStep) _history.RemoveAt(0);
    }

    /// <summary>One plain-English line: which app grew the most over the history window.</summary>
    public string GrowerText()
    {
        if (_history.Count < 2) return "Watching for growers…";
        var oldest = _history[0];
        var latest = _history[^1];
        int minutes = (int)Math.Round((latest.at - oldest.at).TotalMinutes);
        if (minutes < 1) return "Watching for growers…";

        string? bestKey = null;
        long best = 0;
        foreach (var kv in latest.mem)
        {
            oldest.mem.TryGetValue(kv.Key, out long before);
            long delta = kv.Value - before;
            if (delta > best) { best = delta; bestKey = kv.Key; }
        }
        if (bestKey == null || best < GrowerThreshold) return $"No big growers in the last {minutes} min";

        string name = bestKey.Contains('\\')
            ? ResolveName(bestKey, Path.GetFileNameWithoutExtension(bestKey))
            : FriendlyNames.TryGetValue(bestKey, out var f) ? f : bestKey;
        return $"Biggest grower, last {minutes} min: {name} +{Fmt.Bytes(best)}";
    }
}
