using System.Diagnostics;

namespace PhotoTwins.Tests.Support;

/// <summary>
/// A temporary drive letter that points at a folder inside test-work (Windows' own "subst"). Windows has no Recycle Bin for such a
/// letter, which makes it a real test of the "no Recycle Bin" route. Removed again on dispose; only one test at a time uses a letter.
/// </summary>
public sealed class SubstDrive : IDisposable
{
    private static readonly Mutex Gate = new(false, @"Local\PhotoTwins.TestSubst");
    private readonly bool _held;

    public string Letter { get; }
    public string Root => Letter + @":\";

    public SubstDrive(string target)
    {
        Gate.WaitOne();
        _held = true;
        try
        {
            var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            var free = Enumerable.Range('M', 'Z' - 'M' + 1).Select(c => (char)c).Reverse().FirstOrDefault(c => !taken.Contains(c));
            if (free == default) throw new InvalidOperationException("No free drive letter for the subst test.");
            Letter = free.ToString();
            Run($"subst {Letter}: \"{target}\"");
        }
        catch { Gate.ReleaseMutex(); _held = false; throw; }
    }

    private static void Run(string cmd)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{cmd} failed: {o}");
    }

    public void Dispose()
    {
        try { Run($"subst {Letter}: /d"); } catch { /* the drive letter is session-only; it disappears at sign-out anyway */ }
        if (_held) Gate.ReleaseMutex();
    }
}
