using System.Collections.Concurrent;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Tests.Support;

/// <summary>
/// The only file system the scan tests are given. It refuses (loudly) to touch anything outside the test-work folder,
/// which is how "tests only use test photos" is enforced, and it records every file opened so tests can prove which were never opened.
/// It can also pretend files or folders are online-only (cloud) by adding the same attributes Windows uses.
/// </summary>
public sealed class GuardedFileSystem : IFileSystem
{
    private static readonly FileAttributes OnlineBits = (FileAttributes)0x00401000;   // recall-on-data-access + offline
    private readonly RealFileSystem _real = new();
    private readonly string _allowed;

    public ConcurrentQueue<string> Opened { get; } = new();
    public ConcurrentQueue<string> ForbiddenOpens { get; } = new();
    public HashSet<string> OnlineOnlyFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> OnlineOnlyDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files whose listing looks normal but which Windows then reports as online-only just before opening.</summary>
    public HashSet<string> BecameOnlineOnly { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action<string>? BeforeOpen { get; set; }
    public Action<string>? AfterOpen { get; set; }
    public Action<string>? AfterGetRealPath { get; set; }

    private readonly List<string> _alsoAllowed = new();

    public GuardedFileSystem(string allowedRoot) { _allowed = Path.GetFullPath(allowedRoot); }

    /// <summary>For the one test that makes a temporary drive letter pointing INTO test-work: that letter is allowed too.</summary>
    public void AlsoAllow(string root) { lock (_alsoAllowed) _alsoAllowed.Add(Path.GetFullPath(root)); }

    public void Guard(string path)
    {
        var full = Path.GetFullPath(path);
        if (ScanPolicy.IsSameOrInside(full, _allowed)) return;
        lock (_alsoAllowed) if (_alsoAllowed.Any(r => ScanPolicy.IsSameOrInside(full, r))) return;
        throw new InvalidOperationException($"TEST SAFETY: a test tried to touch {full}, which is outside {_allowed}.");
    }

    public bool DirectoryExists(string path) { Guard(path); return _real.DirectoryExists(path); }

    public IEnumerable<FileEntry> Enumerate(string directory)
    {
        Guard(directory);
        foreach (var e in _real.Enumerate(directory))
        {
            if (e.IsDirectory && OnlineOnlyDirs.Contains(e.FullPath)) yield return e with { Attributes = e.Attributes | OnlineBits };
            else if (!e.IsDirectory && OnlineOnlyFiles.Contains(e.FullPath)) yield return e with { Attributes = e.Attributes | OnlineBits };
            else yield return e;
        }
    }

    public FileAttributes GetAttributes(string path)
    {
        Guard(path);
        var a = _real.GetAttributes(path);
        return OnlineOnlyFiles.Contains(path) || BecameOnlineOnly.Contains(path) ? a | OnlineBits : a;
    }

    // Directory metadata for ancestors is needed to validate legacy restore paths; photo reads remain confined to test-work.
    private void GuardMetadata(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full) && ScanPolicy.IsSameOrInside(_allowed, full)) return;
        Guard(path);
    }

    public string GetRealPath(string path) { GuardMetadata(path); var real = _real.GetRealPath(path); AfterGetRealPath?.Invoke(path); return real; }

    public bool IsLink(string path) { GuardMetadata(path); return _real.IsLink(path); }

    public OpenedFile OpenRead(string path)
    {
        Guard(path);
        if (OnlineOnlyFiles.Contains(path) || BecameOnlineOnly.Contains(path))
        {
            ForbiddenOpens.Enqueue(path);
            throw new InvalidOperationException("TEST: an online-only file was opened");
        }
        Opened.Enqueue(path);
        BeforeOpen?.Invoke(path);
        var opened = _real.OpenRead(path);
        AfterOpen?.Invoke(path);
        return opened;
    }
}
