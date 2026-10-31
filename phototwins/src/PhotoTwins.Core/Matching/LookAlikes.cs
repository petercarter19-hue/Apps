using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Matching;

/// <summary>How close two different photos must look, and how close in time, to be called look-alikes (like the shots of a burst).</summary>
public enum Strictness { Strict, Normal, Loose }

public sealed record LookAlikeRule(int PHashBitsWithDate, TimeSpan Window, int PHashBitsWithoutDate);

/// <summary>
/// Look-alikes: DIFFERENT photos that look alike, such as shots taken seconds apart. Off by default. They are never counted as
/// duplicates in the headline and never removed in bulk, only one group at a time.
/// Two photos are look-alikes when their shapes match within 5% and either
///   - both have a date taken, they were taken within the strictness' time window, and their fingerprints are within its bit limit; or
///   - one has no date: their fingerprints are much closer (a lower bit limit) AND they sit in the same folder.
/// The camera may differ (a friend's shot of the same cake can be a look-alike). Groups use the same anchor rule as same-photo
/// groups: every member must be close to the best photo of the group, so a group never drifts from beach to sunset.
/// </summary>
public static class LookAlikes
{
    public const double MaxLogAspectDifference = 0.05;

    public static LookAlikeRule RuleFor(Strictness s) => s switch
    {
        Strictness.Strict => new LookAlikeRule(10, TimeSpan.FromSeconds(10), 6),
        Strictness.Loose => new LookAlikeRule(20, TimeSpan.FromMinutes(5), 14),
        _ => new LookAlikeRule(14, TimeSpan.FromSeconds(60), 10),
    };

    public static Strictness Parse(string? text) =>
        Enum.TryParse<Strictness>(text, ignoreCase: true, out var s) ? s : Strictness.Normal;

    public static bool AreLookAlike(PhotoFile a, PhotoFile b, LookAlikeRule rule) =>
        IsBlank(a) == false && IsBlank(b) == false && Close(a, b, rule);

    private static bool IsBlank(PhotoFile p) => p.Fingerprint == null || FingerprintMath.Spread(p.Fingerprint.Tiny) < SamePhoto.MinTinySpread;

    /// <summary>The cheap part of the rule (blank pictures are filtered out before this is used on many pairs).</summary>
    private static bool Close(PhotoFile a, PhotoFile b, LookAlikeRule rule)
    {
        var fa = a.Fingerprint; var fb = b.Fingerprint;
        if (fa == null || fb == null) return false;
        int bits = FingerprintMath.Distance(fa.PHash, fb.PHash);
        if (a.Info.TakenTicks is { } ta && b.Info.TakenTicks is { } tb)
        {
            if (Math.Abs(ta - tb) > rule.Window.Ticks || bits > rule.PHashBitsWithDate) return false;
        }
        else if (bits > rule.PHashBitsWithoutDate || !string.Equals(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase)) return false;
        return Math.Abs(fa.LogAspect - fb.LogAspect) <= MaxLogAspectDifference;
    }

    /// <summary>Groups of look-alikes among <paramref name="photos"/> (each photo stands for itself: the caller passes group keepers and photos in no group).</summary>
    public static List<List<PhotoFile>> Build(IReadOnlyList<PhotoFile> photos, Strictness strictness, CancellationToken cancellation = default, Action? checkpoint = null)
    {
        var rule = RuleFor(strictness);
        var cand = KeeperRule.Rank(photos.Where(p => !IsBlank(p)).ToList(), _ => false).ToList();
        int m = cand.Count;
        var adj = new List<int>[m];
        var byAspect = Enumerable.Range(0, m).OrderBy(i => cand[i].Fingerprint!.LogAspect).ToArray();
        var aspects = byAspect.Select(i => cand[i].Fingerprint!.LogAspect).ToArray();

        Parallel.For(0, m, new ParallelOptions { CancellationToken = cancellation }, i =>
        {
            checkpoint?.Invoke();
            var list = new List<int>();
            double a = cand[i].Fingerprint!.LogAspect;
            int lo = Array.BinarySearch(aspects, a - MaxLogAspectDifference);
            if (lo < 0) lo = ~lo;
            while (lo > 0 && aspects[lo - 1] >= a - MaxLogAspectDifference) lo--;
            for (int k = lo; k < m && aspects[k] <= a + MaxLogAspectDifference; k++)
            {
                if ((k & 255) == 0) { cancellation.ThrowIfCancellationRequested(); checkpoint?.Invoke(); }
                int j = byAspect[k];
                if (j != i && Close(cand[i], cand[j], rule)) list.Add(j);
            }
            list.Sort();
            adj[i] = list;
        });

        var assigned = new bool[m];
        var groups = new List<List<PhotoFile>>();
        for (int i = 0; i < m; i++)
        {
            cancellation.ThrowIfCancellationRequested(); checkpoint?.Invoke();
            if (assigned[i]) continue;
            assigned[i] = true;
            var members = new List<PhotoFile> { cand[i] };
            foreach (var j in adj[i])
            {
                if (assigned[j]) continue;
                assigned[j] = true;
                members.Add(cand[j]);
            }
            if (members.Count >= 2) groups.Add(members);
        }
        return groups;
    }
}
