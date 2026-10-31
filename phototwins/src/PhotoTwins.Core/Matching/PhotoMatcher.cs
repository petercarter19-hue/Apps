using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Matching;

/// <summary>
/// The exact rule for "same photo": two photos are the same picture saved more than once only when ALL of these hold:
/// their shapes match, their fingerprints match, a closer look at a tiny copy confirms it, AND their hidden photo info
/// (date and time taken, camera) doesn't disagree. A friend's shot of the same cake fails the last test, so it can never
/// count as a copy of mine.
/// </summary>
public static class SamePhoto
{
    /// <summary>Shapes match when width : height agrees within 1.5%.</summary>
    public const double MaxLogAspectDifference = 0.015;
    public const int MaxPHashBits = 6;
    public const int MaxDHashBits = 8;
    /// <summary>After evening out brightness, the 16 x 16 copies may differ by at most this much on average (0 to 255).</summary>
    public const double MaxTinyDifference = 6.0;
    /// <summary>A blank or one-colour picture has no pattern to compare, so it never matches by fingerprint (only as an exact copy).</summary>
    public const double MinTinySpread = 3.0;
    /// <summary>Re-saving doesn't change how bright a picture is; a very different overall brightness is a different picture.</summary>
    public const double MaxMeanBrightnessDifference = 64.0;

    /// <summary>The picture part of the rule (everything except the hidden info).</summary>
    public static bool FingerprintsMatch(Fingerprint a, Fingerprint b)
    {
        if (Math.Abs(a.LogAspect - b.LogAspect) > MaxLogAspectDifference) return false;
        if (FingerprintMath.Distance(a.PHash, b.PHash) > MaxPHashBits) return false;
        if (FingerprintMath.Distance(a.DHash, b.DHash) > MaxDHashBits) return false;
        if (FingerprintMath.Spread(a.Tiny) < MinTinySpread || FingerprintMath.Spread(b.Tiny) < MinTinySpread) return false;
        if (Math.Abs(FingerprintMath.Mean(a.Tiny) - FingerprintMath.Mean(b.Tiny)) > MaxMeanBrightnessDifference) return false;
        return FingerprintMath.TinyDifference(a.Tiny, b.Tiny) <= MaxTinyDifference;
    }

    /// <summary>The whole rule: fingerprints match AND the hidden info doesn't disagree.</summary>
    public static bool IsSamePhoto(Fingerprint fa, PhotoInfo ia, Fingerprint fb, PhotoInfo ib) =>
        FingerprintsMatch(fa, fb) && !InfoRules.Disagree(ia, ib);
}

/// <summary>One picture as it exists on disk: the files with exactly the same bytes (identical copies), and what is known about it.</summary>
public sealed class PhotoVersion
{
    public IReadOnlyList<PhotoFile> Files { get; }
    public Fingerprint? Fingerprint { get; }
    public PhotoInfo Info { get; }

    public PhotoVersion(IReadOnlyList<PhotoFile> files)
    {
        Files = files;
        Fingerprint = files[0].Analysis?.Fingerprint;
        Info = files[0].Analysis?.Info ?? PhotoInfo.None;
    }

    public long Size => Files[0].Size;
    public int Width => Fingerprint?.Width ?? 0;
    public int Height => Fingerprint?.Height ?? 0;
    public long Pixels => Fingerprint?.Pixels ?? 0;
}

/// <summary>One picture saved more than once in different ways (resized, re-saved, another type, sent back from a chat app).</summary>
public sealed class SameGroup
{
    public IReadOnlyList<PhotoVersion> Versions { get; }
    public SameGroup(IReadOnlyList<PhotoVersion> versions) { Versions = versions; }

    public IEnumerable<PhotoFile> AllFiles => Versions.SelectMany(v => v.Files);
    public int FileCount => Versions.Sum(v => v.Files.Count);
    public long TotalBytes => Versions.Sum(v => v.Files.Sum(f => f.Size));

    public int ExtraPhotos => FileCount - 1;
}

public sealed class MatchResult
{
    public IReadOnlyList<ExactGroup> ExactGroups { get; init; } = Array.Empty<ExactGroup>();
    public IReadOnlyList<SameGroup> SameGroups { get; init; } = Array.Empty<SameGroup>();
}

public static class PhotoMatcher
{
    /// <summary>
    /// Sorts photos into groups. Each photo lands in at most one group:
    /// a picture whose only extras are identical files is an exact-copy group; a picture that also exists as a different
    /// version (resized, re-saved, other type) is a same-photo group holding every file of every version.
    /// </summary>
    public static MatchResult Build(IReadOnlyList<PhotoFile> photos, CancellationToken cancellation = default, Action? checkpoint = null)
    {
        // 1. Versions: files with the same size and same SHA-256. Two paths to the very same file (hard links) count once.
        var versions = new List<PhotoVersion>();
        foreach (var g in photos.GroupBy(p => (p.Size, Convert.ToHexString(p.Sha256))))
        {
            cancellation.ThrowIfCancellationRequested(); checkpoint?.Invoke();
            var files = g.GroupBy(p => p.Identity).Select(x => x.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).First())
                         .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
            versions.Add(new PhotoVersion(files));
        }

        // 2. Same-photo families among versions whose picture could be read.
        var cand = versions.Where(v => v.Fingerprint != null)
            .OrderByDescending(v => v.Pixels).ThenByDescending(v => v.Info.TakenTicks != null).ThenBy(v => v.Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
        int m = cand.Count;
        var adj = new List<int>[m];
        var byAspect = Enumerable.Range(0, m).OrderBy(i => cand[i].Fingerprint!.LogAspect).ToArray();
        var aspects = byAspect.Select(i => cand[i].Fingerprint!.LogAspect).ToArray();

        Parallel.For(0, m, new ParallelOptions { CancellationToken = cancellation }, i =>
        {
            checkpoint?.Invoke();
            var list = new List<int>();
            var fi = cand[i].Fingerprint!;
            int lo = Array.BinarySearch(aspects, fi.LogAspect - SamePhoto.MaxLogAspectDifference);
            if (lo < 0) lo = ~lo;
            while (lo > 0 && aspects[lo - 1] >= fi.LogAspect - SamePhoto.MaxLogAspectDifference) lo--;
            for (int k = lo; k < m && aspects[k] <= fi.LogAspect + SamePhoto.MaxLogAspectDifference; k++)
            {
                if ((k & 255) == 0) { cancellation.ThrowIfCancellationRequested(); checkpoint?.Invoke(); }
                int j = byAspect[k];
                if (j == i) continue;
                if (SamePhoto.IsSamePhoto(fi, cand[i].Info, cand[j].Fingerprint!, cand[j].Info)) list.Add(j);
            }
            list.Sort();
            adj[i] = list;
        });

        // 3. Greedy, best picture first: a version joins the anchor's group only if it matches the anchor AND its
        //    hidden info doesn't disagree with anyone already in the group. No "A matches B, B matches C" chains.
        var assigned = new bool[m];
        var sameGroups = new List<SameGroup>();
        var inFamily = new HashSet<PhotoVersion>();
        for (int i = 0; i < m; i++)
        {
            cancellation.ThrowIfCancellationRequested(); checkpoint?.Invoke();
            if (assigned[i]) continue;
            assigned[i] = true;
            var members = new List<int> { i };
            foreach (var j in adj[i])
            {
                if (assigned[j]) continue;
                if (members.Any(k => InfoRules.Disagree(cand[k].Info, cand[j].Info))) continue;
                assigned[j] = true;
                members.Add(j);
            }
            if (members.Count >= 2)
            {
                var vs = members.Select(k => cand[k]).ToList();
                sameGroups.Add(new SameGroup(vs));
                foreach (var v in vs) inFamily.Add(v);
            }
        }

        // 4. Everything else with two or more identical files is an exact-copy group.
        var exact = versions.Where(v => !inFamily.Contains(v) && v.Files.Count >= 2).Select(v => new ExactGroup(v.Files))
            .OrderByDescending(g => g.FreeableBytes).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
        var same = sameGroups.OrderByDescending(g => g.TotalBytes).ThenBy(g => g.Versions[0].Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new MatchResult { ExactGroups = exact, SameGroups = same };
    }
}
