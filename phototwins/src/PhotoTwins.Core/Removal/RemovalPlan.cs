using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Removal;

public enum PlanKind { OneGroup, AllExactCopies, AllSamePhoto }

/// <summary>One group in a plan: the photo that stays, and the photos that go. The keeper is never among the targets.</summary>
public sealed record PlannedGroup(DecidedGroup Group, PhotoFile Keeper, IReadOnlyList<PhotoFile> Targets);

/// <summary>
/// What the user asked to remove. There are only three ways to make one, and nothing else in the app can:
///   - <see cref="ForGroup"/>: "Remove the others" on ONE group (exact copies, same photo or look-alikes);
///   - <see cref="ForAllExactCopies"/>: every exact-copy group at once, only ever exact copies;
///   - <see cref="ForAllSamePhoto"/>: every same-photo group at once, only ever same-photo groups whose photos all have a
///     date taken (a group with a missing date must be checked by eye, one at a time).
///   There is deliberately no way to ask for look-alike groups in bulk.
/// </summary>
public sealed class RemovalPlan
{
    public PlanKind Kind { get; }
    public IReadOnlyList<PlannedGroup> Groups { get; }
    /// <summary>The folders the user picked. Nothing outside them is ever touched.</summary>
    public IReadOnlyList<string> PickedRoots { get; }

    // internal (not private) only so tests can hand the safety gate a plan the public methods could never make
    internal RemovalPlan(PlanKind kind, IReadOnlyList<PlannedGroup> groups, IReadOnlyList<string> roots)
    {
        Kind = kind; Groups = groups; PickedRoots = roots;
    }

    public int PhotoCount => Groups.Sum(g => g.Targets.Count);
    public long Bytes => Groups.Sum(g => g.Targets.Sum(t => t.Size));

    private static PlannedGroup Plan(DecidedGroup g) => new(g, g.Keeper, g.Others.ToList());

    public static RemovalPlan ForGroup(DecidedGroup group, IReadOnlyList<string> pickedRoots) =>
        new(PlanKind.OneGroup, new[] { Plan(group) }, pickedRoots);

    public static RemovalPlan ForAllExactCopies(IEnumerable<DecidedGroup> groups, IReadOnlyList<string> pickedRoots)
    {
        var list = groups.ToList();
        var notExact = list.FirstOrDefault(g => g.Kind != GroupKind.Exact);
        if (notExact != null)
            throw new ArgumentException("Only exact copies can be removed all at once. Same-photo and look-alike groups are removed one group at a time.", nameof(groups));
        return new RemovalPlan(PlanKind.AllExactCopies, list.Where(g => g.Others.Any()).Select(Plan).ToList(), pickedRoots);
    }

    /// <summary>
    /// "Remove all same-photo copies": every same-photo group at once. Refuses (throws) any group that is not a same-photo group, and any
    /// same-photo group where some photo has no date taken: nothing can tell those apart from burst shots, so the user checks them one at a time.
    /// </summary>
    public static RemovalPlan ForAllSamePhoto(IEnumerable<DecidedGroup> groups, IReadOnlyList<string> pickedRoots)
    {
        var list = groups.ToList();
        if (list.Any(g => g.Kind != GroupKind.Same))
            throw new ArgumentException("Only same-photo groups can be removed all at once this way. Exact-copy and look-alike groups have their own routes.", nameof(groups));
        if (list.Any(g => g.SomeHaveNoDateInfo))
            throw new ArgumentException("A same-photo group where some photos have no date info must be checked and removed one group at a time.", nameof(groups));
        return new RemovalPlan(PlanKind.AllSamePhoto, list.Where(g => g.Others.Any()).Select(Plan).ToList(), pickedRoots);
    }

    /// <summary>The same plan without some photos (for "Skip the shared photos"). Groups left with nothing to remove drop out.</summary>
    public RemovalPlan Without(Func<PlannedGroup, PhotoFile, bool> exclude) =>
        new(Kind, Groups.Select(g => g with { Targets = g.Targets.Where(t => !exclude(g, t)).ToList() }).Where(g => g.Targets.Count > 0).ToList(), PickedRoots);
}
