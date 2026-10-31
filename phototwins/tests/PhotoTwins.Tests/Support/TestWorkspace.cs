using System.Diagnostics;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;

namespace PhotoTwins.Tests.Support;

public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) { _a = a; }
    public void Report(T value) => _a(value);
}

/// <summary>
/// One test's private world, entirely inside the app's own test-work folder: a pretend profile folder, a pretend other
/// person's profile, a pretend Google Drive, a Pictures folder to scan, and the app's own data folder.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly List<string> _junctions = new();

    public string Root { get; }
    public string Me { get; }
    public string Other { get; }
    public string FakeGoogleDrive { get; }
    public string Outside { get; }
    public string Pictures { get; }
    public AppPaths Paths { get; }
    public AppLog Log { get; }
    public GuardedFileSystem Fs { get; }
    public ScanPolicy Policy { get; }
    private PhotoCache? _cache;
    public PhotoCache Cache => _cache ??= new PhotoCache(Paths.CacheFile, Log);

    public static string TestWorkRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PhotoTwins.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Cannot find the PhotoTwins folder from the test location.");
        return Path.Combine(dir.FullName, "test-work");
    }

    /// <param name="exactName">Use the name as it is (no random ending), for the showcase's fixed folder.</param>
    /// <param name="profile">The pretend Windows user's folder name.</param>
    public TestWorkspace(string name, bool exactName = false, string profile = "me")
    {
        Root = Path.Combine(TestWorkRoot(), exactName ? name : name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Root);
        Me = Path.Combine(Root, "Users", profile);
        Other = Path.Combine(Root, "Users", "someone-else");
        FakeGoogleDrive = Path.Combine(Root, "GoogleDriveStandIn");
        Outside = Path.Combine(Root, "outside");
        Pictures = Path.Combine(Me, "Pictures");
        foreach (var d in new[] { Me, Other, FakeGoogleDrive, Outside, Pictures }) Directory.CreateDirectory(d);
        Paths = new AppPaths(Path.Combine(Root, "appdata"));
        Log = new AppLog(Paths.LogDir);
        Fs = new GuardedFileSystem(Root);
        Policy = new ScanPolicy(Me, new[] { Other }, p => ScanPolicy.IsSameOrInside(p, FakeGoogleDrive));
    }

    public string Photo(string relativeToPictures, byte[] bytes) => TestPhotoFactory.Write(Path.Combine(Pictures, relativeToPictures), bytes);

    public ScanEngine NewEngine() => new(Fs, Cache, Log, Policy) { MaxParallelReads = 4 };

    public ScanResult Scan(params string[] folders)
    {
        var f = folders.Length == 0 ? new[] { Pictures } : folders;
        return NewEngine().Run(f, new ScanControl(), null);
    }

    /// <summary>A folder link (junction) made with Windows' own mklink, no special rights needed. Used to prove links are never followed.</summary>
    public void MakeJunction(string linkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Run("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{target}\"");
        _junctions.Add(linkPath);
    }

    public void MakeHardLink(string linkPath, string existing) => Run("cmd.exe", $"/c mklink /H \"{linkPath}\" \"{existing}\"");

    private static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} {args} failed: {err}{outp}");
    }

    public void Dispose()
    {
        _cache?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var j in _junctions) { try { Directory.Delete(j); } catch { } }   // remove the link itself first, never what it points to
        try { Directory.Delete(Root, recursive: true); } catch { /* a leftover test folder is harmless and git-ignored */ }
    }
}
