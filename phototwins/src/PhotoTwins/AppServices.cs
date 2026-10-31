using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;
using PhotoTwins.Core.Sharing;

namespace PhotoTwins;

/// <summary>Parts the tests swap for safe stand-ins. In the real app all of these are left out and Windows' real answers are used.</summary>
public sealed class AppServicesOptions
{
    public VolumeRules? Volumes { get; init; }
    public CloudFolderDetector? Cloud { get; init; }
    public SharedFolderDetector? Shared { get; init; }
}

/// <summary>Everything the window needs, built once at start-up (and by the tests with test folders).</summary>
public sealed class AppServices : IDisposable
{
    public AppPaths Paths { get; }
    public AppLog Log { get; }
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public FolderList Folders { get; }
    public IFileSystem Fs { get; }
    public ScanPolicy Policy { get; }
    public PhotoCache Cache { get; }
    public ThumbnailService Thumbnails { get; }
    public ThumbnailStore ThumbStore { get; }
    public UndoJournal Journal { get; }
    public VolumeRules Volumes { get; }
    public CloudFolderDetector Cloud { get; }
    public SharedFolderDetector Shared { get; }
    public FileMutations Mutations { get; }
    public SafetyGate Gate { get; }
    public RemovalService Removal { get; }
    /// <summary>How many photos are read at once. Null = decided by the number of processors.</summary>
    public int? MaxParallelReads { get; set; }

    private readonly List<string> _recoveryNotes = new();

    public AppServices(AppPaths paths, IFileSystem fs, ScanPolicy policy, string picturesFolder, PhotoCache? cache = null, AppServicesOptions? options = null)
    {
        Paths = paths;
        Log = new AppLog(paths.LogDir);
        Store = new SettingsStore(paths, Log);
        Settings = Store.Load();
        Fs = fs;
        Policy = policy;
        Cache = cache ?? new PhotoCache(paths.CacheFile, Log);
        ThumbStore = new ThumbnailStore(paths.ThumbsFile, Log);
        ThumbStore.TrimOld();
        Thumbnails = new ThumbnailService(fs, Log, ThumbStore);
        Folders = new FolderList(Settings, Store, fs, policy);
        Folders.EnsureDefaults(picturesFolder);

        Journal = new UndoJournal(Path.Combine(paths.DataDir, "undo-journal.jsonl"), Log);
        Volumes = options?.Volumes ?? new VolumeRules();
        Cloud = options?.Cloud ?? CloudFolderDetector.ForThisPc(policy, Log);
        Shared = options?.Shared ?? SharedFolderDetector.ForThisPc(Cloud, () => Settings.SharedFolders);
        Folders.SharedDetector = Shared;
        Mutations = new FileMutations(fs, Log, Journal, Volumes);
        Gate = new SafetyGate(fs, Shared, Cloud);
        Removal = new RemovalService(Gate, Mutations, Log);

        // If the app or the PC stopped in the middle of a removal last time, put the record right before anything else happens.
        try { _recoveryNotes.AddRange(Mutations.Reconcile()); }
        catch (Exception ex) { Log.Error("Couldn't check for an interrupted removal", ex); _recoveryNotes.Add("PhotoTwins couldn't check whether a removal was interrupted last time (" + ex.Message + ")."); }
    }

    public DecisionContext DecisionContext => new(Shared, Cloud, () => Settings.KeeperPicks,
        () => Settings.LookAlikesOn ? PhotoTwins.Core.Matching.LookAlikes.Parse(Settings.LookAlikeStrictness) : null);

    /// <summary>Notes to show across the top (damaged settings or cache, an interrupted removal, a log that can't be written). Never silent.</summary>
    public IReadOnlyList<string> StartupNotes()
    {
        var notes = new List<string>();
        if (Store.LoadProblem != null) notes.Add(Store.LoadProblem);
        if (Store.SaveProblem != null) notes.Add(Store.SaveProblem);
        if (Cache.DamagedAndReset) notes.Add("The memory of earlier scans was damaged, so PhotoTwins started a new one. The first scan will take longer than usual.");
        notes.AddRange(_recoveryNotes);
        if (Journal.LoadProblem != null) notes.Add(Journal.LoadProblem);
        if (Log.WriteFailure != null) notes.Add("PhotoTwins couldn't write its log file (" + Log.WriteFailure + "). Problems will still be shown on screen.");
        return notes;
    }

    /// <summary>Remembers the keeper the user chose for a group, so it survives a rescan.</summary>
    public void RememberKeeper(string groupKey, string path)
    {
        Settings.KeeperPicks[groupKey] = path;
        if (Settings.KeeperPicks.Count > 5000)
            foreach (var k in Settings.KeeperPicks.Keys.Take(Settings.KeeperPicks.Count - 5000).ToList()) Settings.KeeperPicks.Remove(k);
        Store.TrySave(Settings);
    }

    public void Dispose()
    {
        Thumbnails.Dispose();  // finish picture work before closing its store or removing the data folder
        Cache.Dispose();
        ThumbStore.Dispose();   // closes pictures.db, so nothing keeps it locked after the app (or a test) is done
    }
}
