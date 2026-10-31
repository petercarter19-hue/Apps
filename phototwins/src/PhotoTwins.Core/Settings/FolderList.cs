using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Sharing;

namespace PhotoTwins.Core.Settings;

public sealed record FolderStatus(string Path, string? Problem, string? SharedReason = null);

/// <summary>The list of folders Pete scans: starts with Pictures, remembered between runs, every pick checked against the folder rules.</summary>
public sealed class FolderList
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly IFileSystem _fs;
    private readonly ScanPolicy _policy;

    public FolderList(AppSettings settings, SettingsStore store, IFileSystem fs, ScanPolicy policy)
    {
        _settings = settings; _store = store; _fs = fs; _policy = policy;
    }

    public IReadOnlyList<string> Folders => _settings.Folders;

    /// <summary>Set by the app so the list can say which folders Windows (or Pete) says are shared.</summary>
    public SharedFolderDetector? SharedDetector { get; set; }

    /// <summary>Folders Pete marked as shared with other people (in case Windows can't see the sharing).</summary>
    public IReadOnlyList<string> SharedFolders => _settings.SharedFolders;

    public bool LookAlikesOn => _settings.LookAlikesOn;
    public Strictness LookAlikeStrictness => PhotoTwins.Core.Matching.LookAlikes.Parse(_settings.LookAlikeStrictness);

    /// <summary>Switches look-alikes on or off and sets how strict they are. Returns a plain reason if it couldn't be saved, or null.</summary>
    public string? SetLookAlikes(bool on, Strictness strictness)
    {
        var (o, s) = (_settings.LookAlikesOn, _settings.LookAlikeStrictness);
        _settings.LookAlikesOn = on; _settings.LookAlikeStrictness = strictness.ToString();
        if (_store.TrySave(_settings)) return null;
        (_settings.LookAlikesOn, _settings.LookAlikeStrictness) = (o, s);
        return _store.SaveProblem;
    }

    /// <summary>Marks a folder as shared. Returns a plain reason if that isn't possible, or null.</summary>
    public string? TryAddShared(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return "Pick a full folder.";
        var full = ScanPolicy.Normalize(path);
        if (!_fs.DirectoryExists(full)) return "That folder doesn't exist (or isn't connected right now).";
        if (_settings.SharedFolders.Any(f => ScanPolicy.IsSameOrInside(full, f))) return "That folder is already marked as shared (or is inside one that is).";
        var before = _settings.SharedFolders.ToList();
        _settings.SharedFolders.RemoveAll(f => ScanPolicy.IsSameOrInside(f, full));
        _settings.SharedFolders.Add(full);
        if (_store.TrySave(_settings)) { SharedDetector?.ForgetEverything(); return null; }
        _settings.SharedFolders.Clear(); _settings.SharedFolders.AddRange(before);
        return _store.SaveProblem;
    }

    public string? RemoveShared(string path)
    {
        var before = _settings.SharedFolders.ToList();
        _settings.SharedFolders.RemoveAll(f => string.Equals(ScanPolicy.Normalize(f), ScanPolicy.Normalize(path), StringComparison.OrdinalIgnoreCase));
        if (_store.TrySave(_settings)) { SharedDetector?.ForgetEverything(); return null; }
        _settings.SharedFolders.Clear(); _settings.SharedFolders.AddRange(before);
        return _store.SaveProblem;
    }

    /// <summary>First run only: offer Windows' Pictures folder (if it's allowed). After that Pete's list is his own, even if empty.</summary>
    public void EnsureDefaults(string picturesFolder)
    {
        if (_settings.FoldersInitialized) return;
        if (!string.IsNullOrWhiteSpace(picturesFolder) && ScanRoots.Check(picturesFolder, _fs, _policy).Ok)
            _settings.Folders.Add(ScanPolicy.Normalize(picturesFolder));
        _settings.FoldersInitialized = true;
        _store.TrySave(_settings);   // if this fails, the start-up notes say so
    }

    /// <summary>Adds a folder. Returns a plain-English reason if it can't be added, or null if it was.</summary>
    public string? TryAdd(string path)
    {
        var chk = ScanRoots.Check(path, _fs, _policy);
        if (!chk.Ok) return chk.Problem;
        var existing = _settings.Folders.FirstOrDefault(f => ScanPolicy.IsSameOrInside(chk.Path, f));
        if (existing != null)
            return string.Equals(ScanPolicy.Normalize(existing), chk.Path, StringComparison.OrdinalIgnoreCase)
                ? "That folder is already in the list."
                : $"That folder is already covered by \"{existing}\".";
        // A bigger folder swallows the smaller ones already listed inside it.
        var before = _settings.Folders.ToList();
        _settings.Folders.RemoveAll(f => ScanPolicy.IsSameOrInside(f, chk.Path));
        _settings.Folders.Add(chk.Path);
        if (_store.TrySave(_settings)) return null;
        _settings.Folders.Clear(); _settings.Folders.AddRange(before);   // not saved, so not changed
        return _store.SaveProblem;
    }

    /// <summary>Removes a folder from the list. Returns a plain-English reason if that couldn't be saved, or null.</summary>
    public string? Remove(string path)
    {
        var before = _settings.Folders.ToList();
        _settings.Folders.RemoveAll(f => string.Equals(ScanPolicy.Normalize(f), ScanPolicy.Normalize(path), StringComparison.OrdinalIgnoreCase));
        if (_store.TrySave(_settings)) return null;
        _settings.Folders.Clear(); _settings.Folders.AddRange(before);
        return _store.SaveProblem;
    }

    /// <summary>Each saved folder with a reason if it can't be scanned right now (gone, disconnected, no longer allowed).</summary>
    public IReadOnlyList<FolderStatus> Describe() =>
        _settings.Folders.Select(f => new FolderStatus(f, ScanRoots.Check(f, _fs, _policy).Problem, SharedDetector?.WhyShared(Path.Combine(f, "x")))).ToList();
}
