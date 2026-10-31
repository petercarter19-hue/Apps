using System.Diagnostics;

namespace Glance;

internal static class ProcessKiller
{
    /// <summary>
    /// Killed: ended. Denied: Windows said no, or wouldn't say whose process it is (an admin copy might still be allowed to look).
    /// Gone: already exited, or the number now belongs to a different program. Refused: it belongs to someone else or another
    /// sign-in session, which no copy of Glance, elevated or not, may end.
    /// </summary>
    public readonly record struct Result(int Killed, int Denied, int Gone, int Refused = 0);

    /// <summary>A process to end, pinned by its start time so a reused process number can't point End at a different program.</summary>
    public readonly record struct Target(int Pid, long Created);

    /// <summary>The Windows calls End makes. Tests replace this with a fake so nothing real is ever ended.</summary>
    internal static IProcApi Api { get; set; } = new WinProcApi();

    public static uint CurrentSession => Api.CurrentSession;

    /// <summary>
    /// The group's own processes plus everything they started, parents first so children can't be respawned mid-kill.
    /// Only this user's own processes in this user's session; never Windows' protected parts, Explorer or Glance itself;
    /// and a process only counts as a child if it started after its parent, because Windows reuses process numbers.
    /// A process that is known to belong to someone else is dropped here, and so are all its children. One whose owner
    /// Windows won't reveal stays in the list: <see cref="Kill"/> makes the final call on the open process and refuses it.
    /// </summary>
    public static List<Target> CollectTree(IEnumerable<int> roots, IReadOnlyDictionary<int, ProcInfo> procs)
    {
        var api = Api;
        string? me = api.CurrentSid;
        var result = new List<Target>();
        if (me == null) return result; // can't tell who "this user" is, so nothing is safe to end

        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, info) in procs)
        {
            if (!procs.TryGetValue(info.Parent, out var parent)) continue;
            if (info.Created == 0 || parent.Created == 0 || info.Created < parent.Created) continue;
            if (!children.TryGetValue(info.Parent, out var list)) children[info.Parent] = list = new List<int>();
            list.Add(pid);
        }

        var seen = new HashSet<int>();
        var rootSet = roots.ToHashSet();
        var queue = new Queue<int>(rootSet);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (!seen.Add(p) || !procs.TryGetValue(p, out var info)) continue;
            if (info.Created == 0 || info.Session != api.CurrentSession || p == Environment.ProcessId) continue;
            if (info.OwnerSid != null && !ProcessRules.SameSid(info.OwnerSid, me)) continue; // someone else's: not this one, nor its children
            if (!rootSet.Contains(p) && (ProcessRules.IsProtectedName(info.Exe) || ProcessRules.IsExplorer(info.Exe))) continue;
            result.Add(new Target(p, info.Created));
            if (children.TryGetValue(p, out var kids))
                foreach (var k in kids) queue.Enqueue(k);
        }
        return result;
    }

    /// <summary>
    /// The one place a process is ended, for every route (the card, the children, the admin helper, the Explorer restart).
    /// Each target is opened once, and everything is checked on that open handle (which pins the process, so its number can't be
    /// reused meanwhile): same start time, not a Windows part, this session, and owned by the account Glance runs as.
    /// If any of that can't be established, it is not ended.
    /// </summary>
    public static Result Kill(IEnumerable<Target> targets, bool allowExplorer = false)
    {
        var api = Api;
        string? me = api.CurrentSid;
        int killed = 0, denied = 0, gone = 0, refused = 0;
        foreach (var t in targets)
        {
            if (t.Pid == Environment.ProcessId || t.Pid <= 4) continue;
            IntPtr h = api.Open(t.Pid, out bool accessDenied);
            if (h == IntPtr.Zero)
            {
                if (accessDenied) denied++; else gone++;
                continue;
            }
            try
            {
                // Same number, different start time: the program we meant has exited and Windows gave its number to another.
                if (!api.GetCreated(h, out long created) || created != t.Created) { gone++; continue; }

                string exe = api.ExeName(h);
                if (ProcessRules.IsProtectedName(exe) || (!allowExplorer && ProcessRules.IsExplorer(exe))) continue;

                // Our own sign-in session only.
                if (!api.SessionOf(t.Pid, out uint session)) { denied++; continue; }
                if (session != api.CurrentSession) { refused++; continue; }

                // Our own account only, read from this same handle right before ending it. Unknown means no.
                if (me == null) { refused++; continue; }
                string? owner = api.OwnerSid(h);
                if (owner == null) { denied++; continue; }
                if (!ProcessRules.SameSid(owner, me)) { refused++; continue; }

                if (api.Terminate(h)) killed++; else denied++;
            }
            finally { api.Close(h); }
        }
        return new Result(killed, denied, gone, refused);
    }

    /// <summary>
    /// One UAC prompt, then a second copy of Glance runs elevated with <c>--end</c> and ends only these exact processes
    /// (checked again by start time, session and owner). Returns how many Windows or Glance still refused, or null if the prompt was cancelled.
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

    /// <summary>
    /// The elevated side of <see cref="KillElevated"/>: arguments are pid:starttime pairs. Same rules as a normal End, checked
    /// again here: only the account this helper runs as (the one that approved the prompt), never another user's processes.
    /// Returns the number still refused.
    /// </summary>
    public static int EndFromArgs(IEnumerable<string> args)
    {
        var targets = new List<Target>();
        foreach (var a in args)
        {
            var parts = a.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int pid) && long.TryParse(parts[1], out long created) && created > 0)
                targets.Add(new Target(pid, created));
        }
        var r = Kill(targets);
        return r.Denied + r.Refused;
    }

    /// <summary>
    /// Ends only this user's explorer.exe itself (never its children: every app launched from the Start menu has Explorer as its parent)
    /// and starts a fresh one if Windows doesn't bring it back on its own.
    /// </summary>
    public static void RestartExplorer(IEnumerable<int> explorerPids, IReadOnlyDictionary<int, ProcInfo> procs)
    {
        var targets = ExplorerTargets(explorerPids, procs);
        Task.Run(() =>
        {
            try { RestartExplorerNow(targets); }
            catch (Exception ex) { Log.Write(ex); }
        });
    }

    internal static List<Target> ExplorerTargets(IEnumerable<int> explorerPids, IReadOnlyDictionary<int, ProcInfo> procs)
    {
        var api = Api;
        string? me = api.CurrentSid;
        if (me == null) return new List<Target>();
        return explorerPids
            .Where(pid => procs.TryGetValue(pid, out var i) && i.Session == api.CurrentSession && i.Created != 0
                          && (i.OwnerSid == null || ProcessRules.SameSid(i.OwnerSid, me)))
            .Select(pid => new Target(pid, procs[pid].Created))
            .ToList();
    }

    /// <summary>The body of <see cref="RestartExplorer"/>, run on a background thread (and directly by tests).</summary>
    internal static void RestartExplorerNow(IReadOnlyCollection<Target> targets)
    {
        var api = Api;
        if (api.CurrentSid == null) return; // can't tell whose Explorer is whose: leave it alone
        Kill(targets, allowExplorer: true);
        // Windows normally restarts the shell itself within a second or two; starting a second one then would just
        // open a File Explorer window. So wait up to 4 s for this user's Explorer before starting one.
        for (int i = 0; i < 16; i++)
        {
            api.Sleep(250);
            if (api.ExplorerRunningFor(api.CurrentSid)) return;
        }
        try { api.StartExplorer(); }
        catch (Exception ex) { Log.Write(ex); }
    }
}
