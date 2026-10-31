// Safety tests for Glance's End button and its test-copy isolation.
//
// These compile the REAL source files (src\ProcessKiller.cs, ProcessRules.cs, Instance.cs, Settings.cs ...) and run them against a
// fake Windows (FakeApi): made-up processes, made-up owners, and a list that records what would have been ended.
// Nothing real is ever ended. The one live check at the end starts a harmless ping.exe that this test launches itself,
// ends exactly that process (by number and start time), and cleans up in a finally block.
//
//   dotnet run --project tools\Tests -c Release
//
// Exit code = number of failed checks.
using System.Diagnostics;
using System.Security.Principal;
using Glance;

// A test copy gets its own names for everything, so nothing here can touch the real Glance, its settings, startup entry or shortcut.
string unitNs = "unit" + Guid.NewGuid().ToString("N")[..8];
string unitDir = Path.Combine(Path.GetTempPath(), "glance-unit-" + unitNs);
Environment.SetEnvironmentVariable("GLANCE_TEST_NS", unitNs);
Environment.SetEnvironmentVariable("GLANCE_TEST_DIR", unitDir);
if (!Instance.IsTest || Instance.TestNs != unitNs) { Console.WriteLine("Refusing to run: test names did not take effect."); return 99; }

int failures = 0, passes = 0;
void Test(string name, Action body)
{
    bool ok;
    try { body(); ok = true; }
    catch (Exception ex) { ok = false; Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}"); }
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (ok) passes++; else failures++;
}
void Expect(bool condition, string what) { if (!condition) throw new Exception("expected: " + what); }

const string Me = "S-1-5-21-1111111111-2222222222-3333333333-1001";
const string Other = "S-1-5-21-1111111111-2222222222-3333333333-1002";

FakeApi Fresh()
{
    var f = new FakeApi();
    ProcessKiller.Api = f;
    return f;
}
FakeApi.P Mine(FakeApi f, int pid, long created, string exe = "notepad") => f.Add(pid, created, session: 1, owner: Me, exe);

try
{
    // ---------------------------------------------------------------- G1: Kill (the one place a process is ended)
    Test("Kill: own process is ended (control)", () =>
    {
        var f = Fresh(); Mine(f, 20101, 1000);
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20101, 1000) });
        Expect(r.Killed == 1 && f.Terminated.SequenceEqual(new[] { 20101 }), "own process ended");
    });

    Test("G1 Kill: foreign owner is refused, never ended", () =>
    {
        var f = Fresh(); f.Add(20102, 2000, session: 1, owner: Other, "notepad");
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20102, 2000) });
        Expect(f.Terminated.Count == 0, "nothing ended");
        Expect(r.Refused == 1 && r.Killed == 0, "counted as refused");
    });

    Test("G1 Kill: unknown owner fails closed (needs admin to look, never ended here)", () =>
    {
        var f = Fresh(); f.Add(20103, 3000, session: 1, owner: null, "notepad");
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20103, 3000) });
        Expect(f.Terminated.Count == 0, "nothing ended");
        Expect(r.Denied == 1 && r.Killed == 0, "counted as denied");
    });

    Test("G1 Kill: owner compare ignores letter case", () =>
    {
        var f = Fresh(); f.Add(20104, 1, session: 1, owner: Me.ToLowerInvariant(), "notepad");
        Expect(ProcessKiller.Kill(new[] { new ProcessKiller.Target(20104, 1) }).Killed == 1, "same account in other case is still the same account");
    });

    Test("G1 Kill: foreign sign-in session is refused", () =>
    {
        var f = Fresh(); f.Add(20105, 5, session: 7, owner: Me, "notepad");
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20105, 5) });
        Expect(f.Terminated.Count == 0 && r.Refused == 1, "refused");
    });

    Test("G1 Kill: unknown session fails closed", () =>
    {
        var f = Fresh(); var p = Mine(f, 20106, 6); p.SessionUnknown = true;
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20106, 6) });
        Expect(f.Terminated.Count == 0 && r.Denied == 1, "denied");
    });

    Test("G1 Kill: if Glance can't tell who it is running as, nothing is ended", () =>
    {
        var f = Fresh(); Mine(f, 20107, 7); f.CurrentSid = null;
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20107, 7) });
        Expect(f.Terminated.Count == 0 && r.Refused == 1, "refused");
    });

    Test("G1 Kill: owner is read from the same open handle, right before ending it", () =>
    {
        var f = Fresh(); Mine(f, 20108, 8);
        ProcessKiller.Kill(new[] { new ProcessKiller.Target(20108, 8) });
        int created = f.Calls.FindIndex(c => c.StartsWith("GetCreated"));
        int owner = f.Calls.FindIndex(c => c.StartsWith("OwnerSid"));
        int term = f.Calls.FindIndex(c => c.StartsWith("Terminate"));
        Expect(created >= 0 && owner > created && term == owner + 1, "start time, then owner, then ending, with nothing in between");
        string handle = f.Calls[owner].Split(' ')[1];
        Expect(f.Calls[created].EndsWith(handle) && f.Calls[term].EndsWith(handle), "all on one handle");
        Expect(f.Calls.Count(c => c.StartsWith("Open ")) == 1, "the process is opened once");
    });

    Test("Kill: wrong start time leaves the process alone", () =>
    {
        var f = Fresh(); Mine(f, 20109, 1000);
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20109, 1001) });
        Expect(f.Terminated.Count == 0 && r.Gone == 1, "left alone");
    });

    Test("Kill: Windows parts and Explorer are skipped (Explorer only when asked)", () =>
    {
        var f = Fresh(); Mine(f, 20110, 1, "lsass"); Mine(f, 20111, 2, "explorer");
        ProcessKiller.Kill(new[] { new ProcessKiller.Target(20110, 1), new ProcessKiller.Target(20111, 2) });
        Expect(f.Terminated.Count == 0, "nothing ended");
        ProcessKiller.Kill(new[] { new ProcessKiller.Target(20111, 2) }, allowExplorer: true);
        Expect(f.Terminated.SequenceEqual(new[] { 20111 }), "explorer ended when allowed");
        Mine(f, 20112, 3, "lsass");
        ProcessKiller.Kill(new[] { new ProcessKiller.Target(20112, 3) }, allowExplorer: true);
        Expect(!f.Terminated.Contains(20112), "protected parts are never ended, even when Explorer is allowed");
    });

    Test("Kill: process that can't be opened is counted, not ended", () =>
    {
        var f = Fresh(); Mine(f, 20113, 1).OpenDenied = true;
        var r = ProcessKiller.Kill(new[] { new ProcessKiller.Target(20113, 1), new ProcessKiller.Target(29999, 1) });
        Expect(r.Denied == 1 && r.Gone == 1 && f.Terminated.Count == 0, "denied and gone");
    });

    // ---------------------------------------------------------------- G1: CollectTree (the row and its children)
    Test("G1 CollectTree: only this user's newer, allowed same-session children come along (control)", () =>
    {
        var f = Fresh();
        var procs = new Dictionary<int, ProcInfo>
        {
            [10101] = new(1, 100, 1, "app", Me),
            [10102] = new(10101, 101, 1, "child", Me),
            [10103] = new(10101, 99, 1, "older-unrelated", Me),
            [10104] = new(10101, 102, 1, "explorer", Me),
            [10105] = new(10101, 102, 1, "lsass", Me),
            [10106] = new(10101, 102, 7, "other-session", Me),
        };
        var tree = ProcessKiller.CollectTree(new[] { 10101 }, procs);
        Expect(tree.SequenceEqual(new[] { new ProcessKiller.Target(10101, 100), new ProcessKiller.Target(10102, 101) }), "root and the one good child");
    });

    Test("G1 CollectTree: a row owned by someone else gives nothing to end", () =>
    {
        Fresh();
        var procs = new Dictionary<int, ProcInfo> { [10201] = new(1, 100, 1, "app", Other), [10202] = new(10201, 101, 1, "child", Me) };
        Expect(ProcessKiller.CollectTree(new[] { 10201 }, procs).Count == 0, "foreign root and its children dropped");
    });

    Test("G1 CollectTree: a foreign-owned child, and everything under it, is dropped", () =>
    {
        Fresh();
        var procs = new Dictionary<int, ProcInfo>
        {
            [10301] = new(1, 100, 1, "app", Me),
            [10302] = new(10301, 101, 1, "run-as-other-user", Other),
            [10303] = new(10302, 102, 1, "grandchild", Me),
            [10304] = new(10301, 103, 1, "good-child", Me),
        };
        var tree = ProcessKiller.CollectTree(new[] { 10301 }, procs);
        Expect(tree.Select(t => t.Pid).OrderBy(x => x).SequenceEqual(new[] { 10301, 10304 }), "only the user's own");
    });

    Test("G1 CollectTree + Kill: a child whose owner is unknown is never ended", () =>
    {
        var f = Fresh();
        f.Add(10401, 100, 1, Me, "app"); f.Add(10402, 101, 1, null, "mystery-child");
        var procs = new Dictionary<int, ProcInfo> { [10401] = new(1, 100, 1, "app", Me), [10402] = new(10401, 101, 1, "mystery-child", null) };
        var tree = ProcessKiller.CollectTree(new[] { 10401 }, procs);
        var r = ProcessKiller.Kill(tree);
        Expect(f.Terminated.SequenceEqual(new[] { 10401 }) && r.Killed == 1 && r.Denied == 1, "the app ends; the unknown child is refused");
    });

    Test("G1 CollectTree: if Glance can't tell who it is running as, nothing is collected", () =>
    {
        var f = Fresh(); f.CurrentSid = null;
        var procs = new Dictionary<int, ProcInfo> { [10501] = new(1, 100, 1, "app", Me) };
        Expect(ProcessKiller.CollectTree(new[] { 10501 }, procs).Count == 0, "empty");
    });

    Test("CollectTree: missing start time or another session's row is dropped", () =>
    {
        Fresh();
        var procs = new Dictionary<int, ProcInfo> { [10601] = new(1, 0, 1, "unknown", Me), [10602] = new(1, 5, 9, "far", Me) };
        Expect(ProcessKiller.CollectTree(new[] { 10601, 10602 }, procs).Count == 0, "dropped");
    });

    // ---------------------------------------------------------------- G1: the elevated helper (--end)
    Test("G1 EndFromArgs: foreign owner is refused and reported", () =>
    {
        var f = Fresh(); f.Add(30101, 4000, 1, Other, "notepad");
        int refused = ProcessKiller.EndFromArgs(new[] { "30101:4000" });
        Expect(f.Terminated.Count == 0 && refused == 1, "not ended, exit code says refused");
    });

    Test("G1 EndFromArgs: unknown owner fails closed", () =>
    {
        var f = Fresh(); f.Add(30102, 4000, 1, null, "notepad");
        int refused = ProcessKiller.EndFromArgs(new[] { "30102:4000" });
        Expect(f.Terminated.Count == 0 && refused == 1, "not ended, exit code says refused");
    });

    Test("G1 EndFromArgs: another sign-in session is refused", () =>
    {
        var f = Fresh(); f.Add(30103, 4000, 5, Me, "notepad");
        int refused = ProcessKiller.EndFromArgs(new[] { "30103:4000" });
        Expect(f.Terminated.Count == 0 && refused == 1, "not ended");
    });

    Test("EndFromArgs: own process is ended; wrong start time and junk arguments are not", () =>
    {
        var f = Fresh(); Mine(f, 30104, 4000);
        Expect(ProcessKiller.EndFromArgs(new[] { "30104:4001", "junk", "30104", "x:y", "30104:0", "30104:-5" }) == 0 && f.Terminated.Count == 0, "wrong time and junk: nothing ended");
        Expect(ProcessKiller.EndFromArgs(new[] { "30104:4000" }) == 0 && f.Terminated.SequenceEqual(new[] { 30104 }), "right time: ended");
    });

    // ---------------------------------------------------------------- G1: Explorer restart
    Test("G1 Explorer restart: a foreign-owned Explorer is not ended, from the list or if forced in", () =>
    {
        var f = Fresh(); f.Add(40101, 10, 1, Other, "explorer");
        var procs = new Dictionary<int, ProcInfo> { [40101] = new(1, 10, 1, "explorer", Other) };
        var targets = ProcessKiller.ExplorerTargets(new[] { 40101 }, procs);
        Expect(targets.Count == 0, "not even listed");
        f.ExplorerRunning = true;
        ProcessKiller.RestartExplorerNow(new[] { new ProcessKiller.Target(40101, 10) });
        Expect(f.Terminated.Count == 0, "and if someone forces it in, Kill still refuses");
    });

    Test("G1 Explorer restart: an Explorer with an unknown owner is not ended", () =>
    {
        var f = Fresh(); f.Add(40102, 10, 1, null, "explorer"); f.ExplorerRunning = true;
        ProcessKiller.RestartExplorerNow(new[] { new ProcessKiller.Target(40102, 10) });
        Expect(f.Terminated.Count == 0, "not ended");
    });

    Test("G1 Explorer restart: if Glance can't tell who it is, it neither ends nor starts anything", () =>
    {
        var f = Fresh(); f.Add(40103, 10, 1, Me, "explorer"); f.CurrentSid = null;
        ProcessKiller.RestartExplorerNow(new[] { new ProcessKiller.Target(40103, 10) });
        Expect(f.Terminated.Count == 0 && f.ExplorerStarts == 0, "nothing");
    });

    Test("Explorer restart: own Explorer is ended; no second one is started if Windows brings it back", () =>
    {
        var f = Fresh(); f.Add(40104, 10, 1, Me, "explorer"); f.ExplorerRunning = true;
        ProcessKiller.RestartExplorerNow(new[] { new ProcessKiller.Target(40104, 10) });
        Expect(f.Terminated.SequenceEqual(new[] { 40104 }) && f.ExplorerStarts == 0, "ended, not started again");
    });

    Test("Explorer restart: own Explorer is ended and a fresh one started if none comes back", () =>
    {
        var f = Fresh(); f.Add(40105, 10, 1, Me, "explorer"); f.ExplorerRunning = false;
        ProcessKiller.RestartExplorerNow(new[] { new ProcessKiller.Target(40105, 10) });
        Expect(f.Terminated.SequenceEqual(new[] { 40105 }) && f.ExplorerStarts == 1, "ended, then started once");
    });

    // ---------------------------------------------------------------- G3 support: test copies are isolated
    Test("G3 Instance: test names are accepted or rejected as documented", () =>
    {
        Expect(Instance.IsValidNs("abc123") && Instance.IsValidNs("A") && Instance.IsValidNs(new string('x', 24)), "good names");
        Expect(!Instance.IsValidNs(null) && !Instance.IsValidNs("") && !Instance.IsValidNs("a b") && !Instance.IsValidNs("a.b") && !Instance.IsValidNs("..\\x") && !Instance.IsValidNs(new string('x', 25)), "bad names");
    });

    Test("G3 Instance: a test copy has its own lock, quit signal, settings folder, startup entry, shortcut and window titles", () =>
    {
        Expect(Instance.MutexName == "Glance.SingleInstance.v1." + unitNs && Instance.MutexName != "Glance.SingleInstance.v1", "lock");
        Expect(Instance.QuitEventName == "Glance.Quit.v1." + unitNs && Instance.QuitEventName != "Glance.Quit.v1", "quit signal");
        Expect(Settings.Dir == unitDir, "settings folder is the test folder");
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance");
        Expect(!Settings.Dir.Equals(real, StringComparison.OrdinalIgnoreCase), "not the real folder");
        Expect(Instance.EntryName == "Glance-test-" + unitNs, "startup entry and shortcut name");
        Expect(Instance.ShortcutDir.StartsWith(unitDir, StringComparison.OrdinalIgnoreCase), "shortcut lives in the test folder, not the Start menu");
        Expect(Instance.CardTitle.Contains(unitNs) && Instance.TrayTitle.Contains(unitNs) && Instance.CardTitle != "Glance" && Instance.TrayTitle != "Glance tray", "window titles");
    });

    Test("G4 Settings.RemoveAll: deletes only Glance's own files, never anything else in the folder", () =>
    {
        Directory.CreateDirectory(unitDir);
        foreach (var n in Settings.OwnFiles) File.WriteAllText(Path.Combine(unitDir, n), "x");
        string decoy = Path.Combine(unitDir, "My vacation photos.jpg");
        string sub = Path.Combine(unitDir, "Documents");
        File.WriteAllText(decoy, "keep me");
        Directory.CreateDirectory(sub); File.WriteAllText(Path.Combine(sub, "taxes.docx"), "keep me too");
        Settings.RemoveAll();
        Expect(Settings.OwnFiles.All(n => !File.Exists(Path.Combine(unitDir, n))), "own files gone");
        Expect(File.Exists(decoy) && File.Exists(Path.Combine(sub, "taxes.docx")), "other files untouched");
        Expect(Directory.Exists(unitDir), "folder stays while it still holds other things");
        File.Delete(decoy); Directory.Delete(sub, true);
        Settings.RemoveAll();
        Expect(!Directory.Exists(unitDir), "folder removed once nothing else is in it");
    });

    // ---------------------------------------------------------------- live, read-only plus one harmless process we start ourselves
    Test("Live: the real Windows calls read this account's id and it matches Windows' own answer", () =>
    {
        string? mine = new WinProcApi().CurrentSid;
        Expect(mine != null && mine == WindowsIdentity.GetCurrent().User?.Value, "same SID");
    });

    Test("Live: real Kill ends exactly the harmless process this test started, and only with the right start time", () =>
    {
        ProcessKiller.Api = new WinProcApi();
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var psi = new ProcessStartInfo(ping, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var child = Process.Start(psi) ?? throw new Exception("could not start the harmless test process");
        DateTime started = child.StartTime;
        try
        {
            Thread.Sleep(300);
            Expect(Native.GetProcessTimes(child.Handle, out long created, out _, out _, out _), "start time readable");
            var wrong = ProcessKiller.Kill(new[] { new ProcessKiller.Target(child.Id, created + 1) });
            Expect(wrong.Gone == 1 && !child.HasExited, "wrong start time: left alone");
            var right = ProcessKiller.Kill(new[] { new ProcessKiller.Target(child.Id, created) });
            Expect(right.Killed == 1 && child.WaitForExit(5000), "right start time: ended");
        }
        finally
        {
            // Clean up exactly the process we started (same number AND same start time), nothing else.
            try { if (!child.HasExited && child.StartTime == started) child.Kill(); } catch { }
        }
    });
}
finally
{
    try { if (Directory.Exists(unitDir)) Directory.Delete(unitDir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{passes} passed, {failures} failed.");
return failures;

namespace Glance
{
    // The app's error log, reduced to a no-op for the tests.
    internal static class Log { public static void Write(Exception? ex) { } }

    /// <summary>A fake Windows: made-up processes with made-up owners. Records every call so tests can check the order.</summary>
    internal sealed class FakeApi : IProcApi
    {
        public sealed class P
        {
            public long Created; public uint Session; public string? Owner; public string Exe = "";
            public bool OpenDenied, SessionUnknown;
        }

        public readonly Dictionary<int, P> Procs = new();
        public readonly List<int> Terminated = new();
        public readonly List<string> Calls = new();
        public bool ExplorerRunning;
        public int ExplorerStarts;
        private readonly Dictionary<IntPtr, int> _handles = new();
        private int _next = 5000;

        public uint CurrentSession { get; set; } = 1;
        public string? CurrentSid { get; set; } = "S-1-5-21-1111111111-2222222222-3333333333-1001";

        public P Add(int pid, long created, uint session, string? owner, string exe) =>
            Procs[pid] = new P { Created = created, Session = session, Owner = owner, Exe = exe };

        public IntPtr Open(int pid, out bool accessDenied)
        {
            accessDenied = false;
            if (!Procs.TryGetValue(pid, out var p)) return IntPtr.Zero;
            if (p.OpenDenied) { accessDenied = true; return IntPtr.Zero; }
            var h = (IntPtr)(++_next);
            _handles[h] = pid;
            Calls.Add($"Open {pid}");
            return h;
        }

        public bool GetCreated(IntPtr process, out long created) { Calls.Add($"GetCreated {(long)process}"); created = Procs[_handles[process]].Created; return true; }
        public string ExeName(IntPtr process) { Calls.Add($"ExeName {(long)process}"); return Procs[_handles[process]].Exe; }
        public string? OwnerSid(IntPtr process) { Calls.Add($"OwnerSid {(long)process}"); return Procs[_handles[process]].Owner; }

        public bool SessionOf(int pid, out uint session)
        {
            Calls.Add($"SessionOf {pid}");
            session = Procs[pid].Session;
            return !Procs[pid].SessionUnknown;
        }

        public bool Terminate(IntPtr process) { Calls.Add($"Terminate {(long)process}"); Terminated.Add(_handles[process]); return true; }
        public void Close(IntPtr process) { Calls.Add($"Close {(long)process}"); }
        public bool ExplorerRunningFor(string? sid) => ExplorerRunning;
        public void StartExplorer() => ExplorerStarts++;
        public void Sleep(int milliseconds) { }
    }
}
