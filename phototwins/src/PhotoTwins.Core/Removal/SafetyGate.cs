using System.Security.Cryptography;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Sharing;

namespace PhotoTwins.Core.Removal;

/// <summary>The answers Pete gave to the confirmation questions. The gate checks that every question it needs was answered "yes".</summary>
public sealed record Confirmations(bool BulkExactCopies = false, bool SharedFolders = false, bool Cloud = false, bool BulkSamePhoto = false);

/// <summary>What a plan will need Pete to confirm before it can go ahead.</summary>
public sealed record PlanNeeds(bool BulkExactCopies, IReadOnlyList<PhotoFile> SharedPhotos, IReadOnlyList<(string Service, int Count)> CloudServices, bool BulkSamePhoto = false);

public sealed record DroppedFile(PhotoFile File, string Why);

public sealed record ApprovedGroup(PlannedGroup Planned, IReadOnlyList<PhotoFile> Targets, string PickedRoot);

public sealed class GateResult
{
    public bool Allowed { get; init; }
    /// <summary>Plain-English reason the whole action was refused, if it was.</summary>
    public string? Refusal { get; init; }
    public IReadOnlyList<ApprovedGroup> Groups { get; init; } = Array.Empty<ApprovedGroup>();
    /// <summary>Photos left out of the action because they changed (or vanished) since the scan. Never silent: listed to Pete.</summary>
    public IReadOnlyList<DroppedFile> Dropped { get; init; } = Array.Empty<DroppedFile>();
    /// <summary>The real locations of the picked folders, for the checks made again just before each photo moves.</summary>
    public IReadOnlyList<string> RealRoots { get; init; } = Array.Empty<string>();

    public static GateResult Refused(string why, IReadOnlyList<DroppedFile>? dropped = null) => new() { Allowed = false, Refusal = why, Dropped = dropped ?? Array.Empty<DroppedFile>() };
}

/// <summary>
/// The last check before anything is removed, run after Pete confirms so nothing can change in between. It refuses the whole
/// action (and says why) if a safety rule would be broken, and quietly-but-visibly leaves out single photos that changed:
///   - every group keeps its keeper: the keeper is never in the list, still exists, and is unchanged (exact copies: same contents);
///   - nothing outside the folders Pete picked (checked on the REAL location, seeing through links and drive-letter shortcuts);
///   - no photo that is itself a shortcut link;
///   - a shared copy always stays when there is one, and removing shared photos needs the second confirm;
///   - removing from a cloud folder needs the cloud warning confirmed; removing many exact copies (or many same-photo copies) at once
///     needs the summary confirmed, and a same-photo bulk plan may hold only same-photo groups whose photos all have a date taken.
/// </summary>
public sealed class SafetyGate
{
    private readonly IFileSystem _fs;
    private readonly SharedFolderDetector? _shared;
    private readonly CloudFolderDetector? _cloud;

    public SafetyGate(IFileSystem fs, SharedFolderDetector? shared, CloudFolderDetector? cloud)
    {
        _fs = fs; _shared = shared; _cloud = cloud;
    }

    private bool IsShared(DecidedGroup g, PhotoFile f) => g.IsShared(f) || (_shared?.IsShared(f.Path) ?? false);
    private string? CloudOf(DecidedGroup g, PhotoFile f) => g.CloudService(f) ?? _cloud?.ServiceOf(f.Path);

    /// <summary>What the plan needs Pete to confirm (used to decide which questions to ask).</summary>
    public PlanNeeds Needs(RemovalPlan plan)
    {
        _shared?.ForgetEverything();
        var shared = plan.Groups.SelectMany(g => g.Targets.Where(t => IsShared(g.Group, t))).ToList();
        var cloud = plan.Groups.SelectMany(g => g.Targets.Select(t => CloudOf(g.Group, t))).Where(s => s != null)
            .GroupBy(s => s!).Select(x => (x.Key, x.Count())).ToList();
        return new PlanNeeds(plan.Kind == PlanKind.AllExactCopies, shared, cloud, plan.Kind == PlanKind.AllSamePhoto);
    }

    public GateResult Check(RemovalPlan plan, Confirmations confirmed)
    {
        if (plan.Groups.Count == 0 || plan.PhotoCount == 0)
            return GateResult.Refused("There is nothing to remove.");

        // The questions that must have been answered "yes".
        var needs = Needs(plan);
        if (needs.BulkExactCopies && !confirmed.BulkExactCopies)
            return GateResult.Refused("Removing many exact copies at once needs your confirmation first, and it wasn't given. Nothing was removed.");
        if (needs.BulkSamePhoto && !confirmed.BulkSamePhoto)
            return GateResult.Refused("Removing many same-photo copies at once needs your confirmation first, and it wasn't given. Nothing was removed.");
        if (needs.SharedPhotos.Count > 0 && !confirmed.SharedFolders)
            return GateResult.Refused("Some of these photos are in a shared folder, which needs a second confirmation that wasn't given. Nothing was removed.");
        if (needs.CloudServices.Count > 0 && !confirmed.Cloud)
            return GateResult.Refused("Removing photos from a cloud folder needs your confirmation first, and it wasn't given. Nothing was removed.");

        // The real locations of the folders Pete picked.
        var realRoots = new List<string>();
        foreach (var root in plan.PickedRoots)
        {
            try { realRoots.Add(_fs.GetRealPath(root)); }
            catch { /* a picked folder that has gone can't contain a target: the inside-the-folders check below will refuse any photo */ }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<DroppedFile>();
        var approved = new List<ApprovedGroup>();

        foreach (var pg in plan.Groups)
        {
            var g = pg.Group;
            if (pg.Targets.Count == 0) continue;

            // --- the rules about the group itself
            if (plan.Kind == PlanKind.AllSamePhoto && g.Kind != GroupKind.Same)
                return GateResult.Refused("PhotoTwins refused: this removal covers same-photo groups only, and the list held a different kind of group. Nothing was removed.");
            if (plan.Kind == PlanKind.AllSamePhoto && g.SomeHaveNoDateInfo)
                return GateResult.Refused("PhotoTwins refused: a group with photos that have no date info can't be removed in bulk. Check it and remove it by itself. Nothing was removed.");
            if (pg.Targets.Any(t => SamePath(t, pg.Keeper)))
                return GateResult.Refused("PhotoTwins refused: the photo to keep was in the list to remove. Nothing was removed.");
            if (!g.Files.Any(f => SamePath(f, pg.Keeper)) || pg.Targets.Any(t => !g.Files.Any(f => SamePath(f, t))))
                return GateResult.Refused("PhotoTwins refused: the list didn't match its group. Nothing was removed.");
            if (g.Files.Count - pg.Targets.Count < 1)
                return GateResult.Refused("PhotoTwins refused: this would remove every copy of a photo. Every group always keeps one. Nothing was removed.");
            if (pg.Targets.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pg.Targets.Count)
                return GateResult.Refused("PhotoTwins refused: a photo was listed twice. Nothing was removed.");
            if (pg.Targets.Any(t => !seen.Add(t.Path)))
                return GateResult.Refused("PhotoTwins refused: a photo is in two groups at once. Nothing was removed.");
            if ((g.HasSharedCopies || IsShared(g, pg.Keeper) || g.Files.Any(f => IsShared(g, f))) && !IsShared(g, pg.Keeper))
                return GateResult.Refused("PhotoTwins refused: the copy in the shared folder always stays, and it wasn't the one chosen to keep. Nothing was removed.");

            // --- the keeper is really there and really unchanged
            var keeperProblem = VerifyKeeper(pg.Keeper, g.Kind == GroupKind.Exact || plan.Kind == PlanKind.AllSamePhoto);   // a bulk removal re-reads every keeper
            if (keeperProblem != null)
                return GateResult.Refused($"PhotoTwins refused: the photo to keep (\"{pg.Keeper.Name}\") {keeperProblem}. Nothing was removed. Scan again to refresh the list.");

            // --- each photo to remove
            string? rootOfGroup = null;
            var ok = new List<PhotoFile>();
            foreach (var t in pg.Targets)
            {
                FileAttributes attrs;
                try { attrs = _fs.GetAttributes(t.Path); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                { dropped.Add(new DroppedFile(t, "It is already gone.")); continue; }
                catch (Exception ex)
                { dropped.Add(new DroppedFile(t, "Windows wouldn't let PhotoTwins look at it: " + ex.Message)); continue; }

                if (CloudAttributes.IsOnlineOnly(attrs)) { dropped.Add(new DroppedFile(t, "It is online-only now, so PhotoTwins won't touch it.")); continue; }
                if (_fs.IsLink(t.Path)) return GateResult.Refused($"PhotoTwins refused: \"{t.Name}\" is a shortcut link, and PhotoTwins never removes links. Nothing was removed.");

                // inside the picked folders, judged on the REAL location
                string real;
                try { real = _fs.GetRealPath(t.Path); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                { dropped.Add(new DroppedFile(t, "It is already gone.")); continue; }
                catch (Exception ex) { return GateResult.Refused($"PhotoTwins refused: it couldn't confirm where \"{t.Name}\" really is ({ex.Message}). Nothing was removed."); }
                var inside = realRoots.FirstOrDefault(r => ScanPolicy.IsSameOrInside(real, r));
                if (inside == null)
                    return GateResult.Refused($"PhotoTwins refused: \"{t.Name}\" is not inside the folders you picked. It never touches anything outside them. Nothing was removed.");
                rootOfGroup ??= PickedRootFor(plan, t.Path, inside);

                // unchanged since the scan
                HandleSnapshot? snap;
                try { snap = Snapshot(t.Path, hashToo: plan.Kind == PlanKind.OneGroup && g.Kind == GroupKind.Exact); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                { dropped.Add(new DroppedFile(t, "It is already gone.")); continue; }
                catch (Exception ex) { dropped.Add(new DroppedFile(t, "It couldn't be read: " + ex.Message)); continue; }
                if (snap.Size != t.Size || snap.LastWriteUtcTicks != t.LastWriteUtcTicks)
                { dropped.Add(new DroppedFile(t, "It changed after the scan. Scan again to include it.")); continue; }
                if (snap.Identity != t.Identity)
                { dropped.Add(new DroppedFile(t, "It is a different file than the one the scan saw. Scan again to include it.")); continue; }
                if (snap.Hash != null && !snap.Hash.AsSpan().SequenceEqual(pg.Keeper.Sha256))
                { dropped.Add(new DroppedFile(t, "It is no longer identical to the photo being kept. Scan again to refresh the list.")); continue; }
                ok.Add(t);
            }
            if (ok.Count > 0) approved.Add(new ApprovedGroup(pg with { Targets = ok }, ok, rootOfGroup ?? plan.PickedRoots.First()));
        }

        if (approved.Count == 0)
            return GateResult.Refused("Nothing was removed: " + (dropped.Count > 0 ? "every photo had changed or gone since the scan. Scan again." : "there was nothing to remove."), dropped);
        return new GateResult { Allowed = true, Groups = approved, Dropped = dropped, RealRoots = realRoots };
    }

    /// <summary>
    /// The quick check made again JUST BEFORE each photo is moved (a long removal can take a long time, and another program such as
    /// OneDrive may change things meanwhile): the photo to keep is still there, unchanged and not online-only, and the photo to
    /// remove hasn't changed or moved. Returns a plain reason to leave this photo alone, or null. <paramref name="keeperProblem"/>
    /// says the trouble is with the photo to keep (so the rest of that group must be left alone too).
    /// </summary>
    public string? Recheck(ApprovedGroup group, PhotoFile target, IReadOnlyList<string> realRoots, out bool keeperProblem)
    {
        keeperProblem = false;
        var keeper = group.Planned.Keeper;
        var kp = QuickLook(keeper);
        if (kp != null) { keeperProblem = true; return $"the photo to keep (\"{keeper.Name}\") {kp}"; }
        var tp = QuickLook(target);
        if (tp != null) return $"it {tp}";
        try
        {
            if (_fs.IsLink(target.Path)) return "it is now a shortcut link";
            var real = _fs.GetRealPath(target.Path);
            if (!realRoots.Any(r => ScanPolicy.IsSameOrInside(real, r))) return "it is no longer inside the folders you picked";
        }
        catch (Exception ex) { return "it couldn't be checked (" + ex.Message + ")"; }
        return null;
    }

    /// <summary>Still there, not online-only, and the same file as the scan saw (size, time, Windows file ID). Null if so.</summary>
    private string? QuickLook(PhotoFile f)
    {
        try
        {
            if (CloudAttributes.IsOnlineOnly(_fs.GetAttributes(f.Path))) return "is online-only now";
            using var open = _fs.OpenRead(f.Path);
            if (open.Info.Size != f.Size || open.Info.LastWriteUtcTicks != f.LastWriteUtcTicks) return "changed while the removal was running";
            if (open.Info.Identity != f.Identity) return "was replaced by a different file while the removal was running";
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "is no longer there"; }
        catch (Exception ex) { return "couldn't be checked (" + ex.Message + ")"; }
    }

    private static bool SamePath(PhotoFile a, PhotoFile b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>The picked folder (as Pete picked it) that holds this photo: the longest one that contains it.</summary>
    private string PickedRootFor(RemovalPlan plan, string path, string realRoot)
    {
        var textual = plan.PickedRoots.Where(r => ScanPolicy.IsSameOrInside(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (textual != null) return textual;
        foreach (var r in plan.PickedRoots)
        {
            try { if (string.Equals(_fs.GetRealPath(r), realRoot, StringComparison.OrdinalIgnoreCase)) return r; } catch { }
        }
        return plan.PickedRoots.First();
    }

    private sealed record HandleSnapshot(long Size, long LastWriteUtcTicks, Native.FileIdentity Identity, byte[]? Hash);

    private HandleSnapshot Snapshot(string path, bool hashToo)
    {
        using var open = _fs.OpenRead(path);
        byte[]? hash = hashToo ? SHA256.HashData(open.Stream) : null;
        return new HandleSnapshot(open.Info.Size, open.Info.LastWriteUtcTicks, open.Info.Identity, hash);
    }

    /// <summary>Null if the keeper is fine; otherwise a short plain reason, e.g. "is no longer there".</summary>
    private string? VerifyKeeper(PhotoFile keeper, bool reRead)
    {
        try
        {
            if (CloudAttributes.IsOnlineOnly(_fs.GetAttributes(keeper.Path))) return "is online-only now";
            var s = Snapshot(keeper.Path, hashToo: reRead);
            if (s.Size != keeper.Size || s.LastWriteUtcTicks != keeper.LastWriteUtcTicks) return "has changed since the scan";
            if (s.Identity != keeper.Identity) return "is a different file than the scan saw";
            if (s.Hash != null && !s.Hash.AsSpan().SequenceEqual(keeper.Sha256)) return "no longer has the same contents";
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "is no longer there"; }
        catch (Exception ex) { return "couldn't be checked (" + ex.Message + ")"; }
    }
}
