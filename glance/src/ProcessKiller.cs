using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

internal static class ProcessKiller
{
    public readonly record struct Result(int Killed, int Denied, int Gone);

    /// <summary>A process to end, pinned by its start time so a reused process number can't point End at a different program.</summary>
    public readonly record struct Target(int Pid, long Created);

    public static readonly uint CurrentSession = (uint)Process.GetCurrentProcess().SessionId;

    /// <summary>
    /// The group's own processes plus everything they started, parents first so children can't be respawned mid-kill.
    /// Only this user's session; never Windows' protected parts, Explorer or Glance itself; and a process only counts as a
    /// child if it started after its parent, because Windows reuses process numbers once a parent has exited.
    /// </summary>
    public static List<Target> CollectTree(IEnumerable<int> roots, IReadOnlyDictionary<int, ProcInfo> procs)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, info) in procs)
        {
            if (!procs.TryGetValue(info.Parent, out var parent)) continue;
            if (info.Created == 0 || parent.Created == 0 || info.Created < parent.Created) continue;
            if (!children.TryGetValue(info.Parent, out var list)) children[info.Parent] = list = new List<int>();
            list.Add(pid);
        }

        var result = new List<Target>();
        var seen = new HashSet<int>();
        var rootSet = roots.ToHashSet();
        var queue = new Queue<int>(rootSet);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (!seen.Add(p) || !procs.TryGetValue(p, out var info)) continue;
            if (info.Created == 0 || info.Session != CurrentSession || p == Environment.ProcessId) continue;
            if (!rootSet.Contains(p) && (ProcessScanner.IsProtectedName(info.Exe) || IsExplorer(info.Exe))) continue;
            result.Add(new Target(p, info.Created));
            if (children.TryGetValue(p, out var kids))
                foreach (var k in kids) queue.Enqueue(k);
        }
        return result;
    }

    public static Result Kill(IEnumerable<Target> targets, bool allowExplorer = false)
    {
        int killed = 0, denied = 0, gone = 0;
        var path = new StringBuilder(1024);
        foreach (var t in targets)
        {
            if (t.Pid == Environment.ProcessId || t.Pid <= 4) continue;
            IntPtr h = Native.OpenProcess(Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, t.Pid);
            if (h == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 5) denied++; else gone++;
                continue;
            }
            try
            {
                // Same number, different start time: the program we meant has exited and Windows gave its number to another.
                if (!Native.GetProcessTimes(h, out long created, out _, out _, out _) || created != t.Created) { gone++; continue; }

                int cap = path.Capacity;
                path.Clear();
                string exe = Native.QueryFullProcessImageName(h, 0, path, ref cap) ? Path.GetFileNameWithoutExtension(path.ToString()) : "";
                if (ProcessScanner.IsProtectedName(exe) || (!allowExplorer && IsExplorer(exe))) continue;

                if (Native.TerminateProcess(h, 1)) killed++; else denied++;
            }
            finally { Native.CloseHandle(h); }
        }
        return new Result(killed, denied, gone);
    }

    /// <summary>
    /// One UAC prompt, then a second copy of Glance runs elevated with <c>--end</c> and ends only these exact processes
    /// (checked again by start time). Returns how many Windows still refused, or null if the prompt was cancelled.
    /// </summary>
    public static int? KillElevated(IReadOnlyCollection<Target> targets)
    {
        string? exe = Environment.ProcessPath;
        if (targets.Count == 0 || exe == null) return 0;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--end " + string.Join(" ", targets.Select(t => $"{t.Pid}:{t.Created}")))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // the UAC prompt was cancelled
        }
    }

    /// <summary>The elevated side of <see cref="KillElevated"/>: arguments are pid:starttime pairs. Returns the number refused.</summary>
    public static int EndFromArgs(IEnumerable<string> args)
    {
        var targets = new List<Target>();
        foreach (var a in args)
        {
            var parts = a.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && long.TryParse(parts[1], out long created) && created > 0)
                targets.Add(new Target(pid, created));
        }
        return Kill(targets).Denied;
    }

    /// <summary>
    /// Ends only explorer.exe itself (never its children: every app launched from the Start menu has Explorer as its parent)
    /// and starts a fresh one if Windows doesn't bring it back on its own.
    /// </summary>
    public static void RestartExplorer(IEnumerable<int> explorerPids, IReadOnlyDictionary<int, ProcInfo> procs)
    {
        var targets = explorerPids
            .Where(pid => procs.TryGetValue(pid, out var i) && i.Session == CurrentSession && i.Created != 0)
            .Select(pid => new Target(pid, procs[pid].Created))
            .ToList();
        Task.Run(() =>
        {
            Kill(targets, allowExplorer: true);
            // Windows normally restarts the shell itself within a second or two; starting a second one then would just
            // open a File Explorer window. So wait up to 4 s for this user's Explorer before starting one.
            for (int i = 0; i < 16; i++)
            {
                Thread.Sleep(250);
                if (ExplorerRunningHere()) return;
            }
            try
            {
                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) { Log.Write(ex); }
        });
    }

    private static bool ExplorerRunningHere()
    {
        Process[] running = Process.GetProcessesByName("explorer");
        try { return running.Any(p => p.SessionId == CurrentSession); }
        finally { foreach (var p in running) p.Dispose(); }
    }

    private static bool IsExplorer(string exeNoExt) => exeNoExt.Equals("explorer", StringComparison.OrdinalIgnoreCase);
}
