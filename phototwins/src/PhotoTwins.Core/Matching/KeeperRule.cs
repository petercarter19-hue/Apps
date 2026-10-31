using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Sharing;

namespace PhotoTwins.Core.Matching;

/// <summary>
/// Picks the photo to keep in a group (the one with the star). In this order, each step only breaks ties left by the one before:
///   0. A photo in a shared folder always wins (and only shared photos compete for it).
///   1. The most pixels (width x height): the biggest PICTURE, never the biggest file.
///   2. The original over a copy: names like "(1)", "copy", "edited", chat-app names, a converted file, or a file in a
///      "Converted" or "Edited" folder count as copies.
///   3. The one with its date taken and place info.
///   4. The oldest (the earlier of when it was created and last changed).
///   5. So the pick never changes between runs: fewer folder levels, then alphabetical path.
/// </summary>
public static class KeeperRule
{
    private static readonly HashSet<string> CameraTypes = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".heic", ".heif" };
    private static readonly string[] CopyFolders = { "converted", "edited", "edits", "copies", "copy" };

    private static readonly Regex[] CopyNames =
    {
        new(@"\(\d+\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),                     // IMG_1 (1)
        new(@"\bcopy\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),                        // IMG_1 copy, Copy of IMG_1, IMG_1 - Copy
        new(@"(^|[\s_\-.])edited($|[\s_\-.])", RegexOptions.IgnoreCase | RegexOptions.Compiled),  // IMG_1-edited, edited_IMG_1
        new(@"\b(converted|resized)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"~\d+$", RegexOptions.Compiled),                                                     // IMG_1~2
        new(@"^IMG-\d{8}-WA\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled),                // WhatsApp
        new(@"WA\d{4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^received_", RegexOptions.IgnoreCase | RegexOptions.Compiled),                      // Messenger
        new(@"\d+_\d+_\d+_n$", RegexOptions.IgnoreCase | RegexOptions.Compiled),                  // Facebook
    };

    public static bool IsCopyName(string fileName) =>
        CopyNames.Any(r => r.IsMatch(Path.GetFileNameWithoutExtension(fileName)));

    public static bool InCopyFolder(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        return dir.Split('\\', '/').Any(seg => CopyFolders.Contains(seg.Trim().ToLowerInvariant()));
    }

    /// <summary>A picture file type that a camera doesn't make (PNG, BMP, TIFF, WEBP) sitting next to a camera type is a conversion.</summary>
    public static bool IsConverted(PhotoFile file, IReadOnlyList<PhotoFile> group)
    {
        var ext = Path.GetExtension(file.Path);
        if (CameraTypes.Contains(ext)) return false;
        return group.Any(o => CameraTypes.Contains(Path.GetExtension(o.Path)));
    }

    public static bool LooksLikeACopy(PhotoFile file, IReadOnlyList<PhotoFile> group) =>
        IsCopyName(file.Name) || InCopyFolder(file.Path) || IsConverted(file, group);

    private static int InfoScore(PhotoFile f) => (f.Info.TakenTicks != null ? 1 : 0) + (f.Info.HasGps ? 1 : 0);

    private static long Oldest(PhotoFile f)
    {
        long c = f.CreatedUtcTicks > 0 ? f.CreatedUtcTicks : long.MaxValue;
        return Math.Min(c, f.LastWriteUtcTicks);
    }

    private static int Depth(PhotoFile f) => f.Path.Count(ch => ch == '\\');

    /// <summary>All photos of the group, best keeper first. <paramref name="isShared"/> says which are in shared folders.</summary>
    public static IReadOnlyList<PhotoFile> Rank(IReadOnlyList<PhotoFile> files, Func<PhotoFile, bool> isShared) =>
        files.OrderByDescending(isShared)
             .ThenByDescending(f => f.Fingerprint?.Pixels ?? 0)
             .ThenByDescending(f => !LooksLikeACopy(f, files))
             .ThenByDescending(InfoScore)
             .ThenBy(Oldest)
             .ThenBy(Depth)
             .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
             .ToList();
}

public enum GroupKind { Exact, Same, LookAlike }

/// <summary>
/// A group of duplicates with its keeper decided. This is what the screen shows and what a removal plan is built from.
/// The keeper can be moved by Pete, but never off the shared copies when there are any.
/// </summary>
public sealed class DecidedGroup
{
    private readonly List<PhotoFile> _files;
    private readonly Dictionary<string, string> _shared;
    private readonly IReadOnlyDictionary<string, string> _cloud;

    public DecidedGroup(GroupKind kind, IReadOnlyList<PhotoFile> ranked, IReadOnlyList<PhotoVersion>? versions,
        IDictionary<string, string> sharedReasons, IReadOnlyDictionary<string, string> cloudServices)
    {
        Kind = kind;
        _files = ranked.ToList();
        Versions = versions;
        _shared = new Dictionary<string, string>(sharedReasons, StringComparer.OrdinalIgnoreCase);
        _cloud = new Dictionary<string, string>(cloudServices, StringComparer.OrdinalIgnoreCase);
        Keeper = _files[0];
        Key = KeyOf(_files);
    }

    public GroupKind Kind { get; }
    public IReadOnlyList<PhotoVersion>? Versions { get; }
    /// <summary>Best keeper first, then the rest in rank order.</summary>
    public IReadOnlyList<PhotoFile> Files => _files;
    public PhotoFile Keeper { get; private set; }
    public bool KeeperChosenByUser { get; private set; }
    /// <summary>A stable name for this group (made from the contents of its photos), so Pete's choice of keeper survives a rescan.</summary>
    public string Key { get; }

    public bool IsShared(PhotoFile f) => _shared.ContainsKey(f.Path);
    public string? SharedReason(PhotoFile f) => _shared.TryGetValue(f.Path, out var r) ? r : null;
    public bool HasSharedCopies => _files.Any(IsShared);
    public string? CloudService(PhotoFile f) => _cloud.TryGetValue(f.Path, out var s) ? s : null;

    public IEnumerable<PhotoFile> Others => _files.Where(f => !SamePath(f, Keeper));
    public int ExtraPhotos => _files.Count - 1;
    public long FreeableBytes => Others.Sum(f => f.Size);
    public bool StillAGroup => _files.Count >= 2;

    /// <summary>True when some photo in a same-photo group has no date taken (nothing can tell burst shots apart then).</summary>
    public bool SomeHaveNoDateInfo => Kind == GroupKind.Same && _files.Any(f => f.Info.TakenTicks == null);

    private static bool SamePath(PhotoFile a, PhotoFile b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves the star. Refused (false) if the group has shared copies and this photo isn't one of them.</summary>
    public bool TrySetKeeper(PhotoFile file, bool byUser = true)
    {
        var f = _files.FirstOrDefault(x => SamePath(x, file));
        if (f == null) return false;
        if (HasSharedCopies && !IsShared(f)) return false;
        Keeper = f;
        KeeperChosenByUser = byUser;
        return true;
    }

    /// <summary>
    /// Takes photos out of the group after they were removed from disk, whoever they were in this group. If the keeper was one of them
    /// (a look-alike removal can take another group's keeper), the best of the rest becomes the keeper.
    /// </summary>
    public void RemoveFiles(IEnumerable<string> removedPaths)
    {
        var gone = new HashSet<string>(removedPaths, StringComparer.OrdinalIgnoreCase);
        if (!_files.Any(f => gone.Contains(f.Path))) return;
        bool keeperGone = gone.Contains(Keeper.Path);
        for (int i = 0; i < _files.Count; i++)   // remember each photo, its place, and where the star was, for Undo
            if (gone.Contains(_files[i].Path)) _history.Add(new TakenOut(_files[i], i, Keeper, KeeperChosenByUser, keeperGone));
        _files.RemoveAll(f => gone.Contains(f.Path));
        if (_files.Count == 0) return;
        if (keeperGone)
        {
            var ranked = KeeperRule.Rank(_files, f => _shared.ContainsKey(f.Path));
            Keeper = ranked[0];
            KeeperChosenByUser = false;
        }
    }

    private sealed record TakenOut(PhotoFile File, int Index, PhotoFile PrevKeeper, bool PrevChosen, bool KeeperWasTakenOut);
    private readonly List<TakenOut> _history = new();

    /// <summary>
    /// Puts photos that were taken out back into the group, each in the place it was in, and the star back where it was if the removal had
    /// moved it. <paramref name="refresh"/> gives the photo's up-to-date details (null = leave it out). Returns the paths put back.
    /// </summary>
    public IReadOnlyList<string> RestoreFiles(IEnumerable<string> paths, Func<PhotoFile, PhotoFile?>? refresh = null)
    {
        var want = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var done = new List<string>();
        foreach (var h in _history.Where(h => want.Contains(h.File.Path)).OrderBy(h => h.Index).ToList())
        {
            if (_files.Any(f => SamePath(f, h.File))) { _history.Remove(h); continue; }
            var file = refresh == null ? h.File : refresh(h.File);
            if (file == null) continue;
            _files.Insert(Math.Min(h.Index, _files.Count), file);
            _history.Remove(h);
            done.Add(file.Path);
            if (h.KeeperWasTakenOut && SamePath(h.PrevKeeper, h.File)) { Keeper = file; KeeperChosenByUser = h.PrevChosen; }
        }
        if (done.Count > 0 && !_files.Any(f => SamePath(f, Keeper)))
        {
            Keeper = KeeperRule.Rank(_files, f => _shared.ContainsKey(f.Path))[0];   // (the group had been emptied)
            KeeperChosenByUser = false;
        }
        // a refreshed copy of the keeper itself replaces the old details
        var kf = _files.FirstOrDefault(f => SamePath(f, Keeper));
        if (kf != null) Keeper = kf;
        return done;
    }

    /// <summary>Takes photos out of the group after they were removed. The keeper itself can never be taken out here.</summary>
    public void Forget(IEnumerable<string> removedPaths)
    {
        var gone = new HashSet<string>(removedPaths, StringComparer.OrdinalIgnoreCase);
        gone.Remove(Keeper.Path);
        _files.RemoveAll(f => gone.Contains(f.Path));
    }

    public static string KeyOf(IEnumerable<PhotoFile> files)
    {
        var ids = files.Select(f => $"{f.Size}-{Convert.ToHexString(f.Sha256)}").Distinct().OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", ids))))[..20];
    }
}

/// <summary>What the scan needs to know to decide keepers: which folders are shared or cloud, and which keepers Pete picked before.</summary>
/// <param name="LookAlikes">Returns how strict to be about look-alikes, or null when they are switched off (the default).</param>
public sealed record DecisionContext(SharedFolderDetector? Shared, CloudFolderDetector? Cloud, Func<IReadOnlyDictionary<string, string>> Picks, Func<Strictness?>? LookAlikes = null);

public static class GroupDecider
{
    /// <summary>Gives every group its keeper (the rule above), notes which photos are in shared and cloud folders, and applies Pete's earlier picks.</summary>
    public static List<DecidedGroup> Decide(MatchResult matches, SharedFolderDetector? shared, CloudFolderDetector? cloud, IReadOnlyDictionary<string, string>? picks, Action? checkpoint = null)
    {
        var list = new List<DecidedGroup>();
        foreach (var g in matches.ExactGroups) list.Add(Make(GroupKind.Exact, g.Files, null, shared, cloud, picks, checkpoint));
        foreach (var g in matches.SameGroups) list.Add(Make(GroupKind.Same, g.AllFiles.ToList(), g.Versions, shared, cloud, picks, checkpoint));
        return list;
    }

    /// <summary>Turns groups of look-alike photos into decided groups (with keepers, shared and cloud notes, and Pete's earlier picks).</summary>
    public static List<DecidedGroup> DecideLookAlikes(IEnumerable<List<PhotoFile>> groups, SharedFolderDetector? shared, CloudFolderDetector? cloud, IReadOnlyDictionary<string, string>? picks, Action? checkpoint = null) =>
        groups.Select(g => Make(GroupKind.LookAlike, g, null, shared, cloud, picks, checkpoint)).ToList();

    private static DecidedGroup Make(GroupKind kind, IReadOnlyList<PhotoFile> files, IReadOnlyList<PhotoVersion>? versions,
        SharedFolderDetector? shared, CloudFolderDetector? cloud, IReadOnlyDictionary<string, string>? picks, Action? checkpoint)
    {
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var services = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            checkpoint?.Invoke();
            if (shared?.WhyShared(f.Path) is { } why) reasons[f.Path] = why;
            if (cloud?.ServiceOf(f.Path) is { } svc) services[f.Path] = svc;
        }
        var ranked = KeeperRule.Rank(files, f => reasons.ContainsKey(f.Path));
        var group = new DecidedGroup(kind, ranked, versions, reasons, services);
        if (picks != null && picks.TryGetValue(group.Key, out var path))
        {
            var f = group.Files.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (f != null) group.TrySetKeeper(f, byUser: true);
        }
        return group;
    }
}
