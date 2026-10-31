using System.Security.Cryptography;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Sharing;
using PhotoTwins.Core.Native;

namespace PhotoTwins.Tests.Support;

/// <summary>Everything a removal test needs, wired the way the app wires it, inside one private test-work folder.</summary>
public sealed class RemovalHarness : IDisposable
{
    public TestWorkspace Ws { get; }
    public UndoJournal Journal { get; }
    public FileMutations Mutations { get; }
    public SafetyGate Gate { get; }
    public RemovalService Service { get; }
    public VolumeRules Volumes { get; }
    public SharedFolderDetector Shared { get; }
    public CloudFolderDetector Cloud { get; }
    public List<string> SharedMarked { get; } = new();
    public Dictionary<string, string> Picks { get; } = new();
    public IReadOnlyList<string> Roots => new[] { Ws.Pictures };
    /// <summary>Null = look-alikes off (the default in the real app).</summary>
    public Strictness? LookAlikes { get; set; }

    /// <param name="cloudFolders">Given the Pictures folder, names the folders to treat as cloud (OneDrive) folders.</param>
    public RemovalHarness(string name, VolumeRules? volumes = null, Func<string, IEnumerable<string>>? cloudFolders = null)
    {
        Ws = new TestWorkspace(name);
        Journal = new UndoJournal(Path.Combine(Ws.Paths.DataDir, "undo-journal.jsonl"), Ws.Log);
        Volumes = volumes ?? new VolumeRules();
        Shared = new SharedFolderDetector(Array.Empty<Func<string, string?>>(), () => SharedMarked);
        Cloud = new CloudFolderDetector((cloudFolders?.Invoke(Ws.Pictures) ?? Array.Empty<string>()).Select(p => new CloudRoot(p, "OneDrive")));
        Mutations = new FileMutations(Ws.Fs, Ws.Log, Journal, Volumes);
        Gate = new SafetyGate(Ws.Fs, Shared, Cloud);
        Service = new RemovalService(Gate, Mutations, Ws.Log);
    }

    public ScanResult Scan(params string[] folders)
    {
        var engine = new ScanEngine(Ws.Fs, Ws.Cache, Ws.Log, Ws.Policy, null, new DecisionContext(Shared, Cloud, () => Picks, () => LookAlikes)) { MaxParallelReads = 4 };
        return engine.Run(folders.Length == 0 ? Roots : folders, new ScanControl(), null);
    }

    /// <summary>Two or more identical photos, returns their paths.</summary>
    public string[] Identical(int seed, params string[] relativePaths)
    {
        var bytes = TestPhotoFactory.StandIn(seed, 5000);
        return relativePaths.Select(p => Ws.Photo(p, bytes)).ToArray();
    }

    public RemovalResult Run(RemovalPlan plan, Confirmations? c = null) => Service.Execute(plan, c ?? new Confirmations(), null, CancellationToken.None);

    public RemovalPlan PlanFor(DecidedGroup g) => RemovalPlan.ForGroup(g, Roots);

    public static FileSnapshot Snap(string path) => FileSnapshot.Of(path);

    public void Dispose()
    {
        // leave the real Recycle Bin as found: put back anything this test recycled, then drop the test folder
        try
        {
            foreach (var e in Journal.Entries().Where(x => x.Status == EntryStatus.Done && x.Method == RemoveMethod.Recycle))
                Mutations.PutBack(e);
        }
        catch { /* best effort */ }
        Ws.Dispose();
    }
}

/// <summary>Everything about a file that would change if the app touched it.</summary>
public sealed record FileSnapshot(string Sha, long Size, DateTime LastWriteUtc, DateTime CreatedUtc, FileAttributes Attributes, FileIdentity Identity)
{
    public static FileSnapshot Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = HandleInfoReader.Read(fs.SafeFileHandle);
        var sha = Convert.ToHexString(SHA256.HashData(fs));
        var fi = new FileInfo(path);
        return new FileSnapshot(sha, fi.Length, fi.LastWriteTimeUtc, fi.CreationTimeUtc, fi.Attributes, info.Identity);
    }
}
