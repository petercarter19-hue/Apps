using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Sharing;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class SharingAliasTests
{
    [Fact]
    public void A_selected_alias_to_a_cloud_and_shared_folder_keeps_the_shared_copy_and_requires_warnings()
    {
        using var h = new RemovalHarness("sharing-alias", new NoBinRules(), pics => new[] { Path.Combine(pics, "Cloud") });
        h.Identical(910, "Cloud\\a.jpg", "Cloud\\b.jpg");
        var physical = Path.Combine(h.Ws.Pictures, "Cloud");
        h.SharedMarked.Add(physical);
        var alias = Path.Combine(h.Ws.Root, "Alias");
        h.Ws.MakeJunction(alias, physical);
        var group = Assert.Single(h.Scan(alias).Decided);
        var plan = RemovalPlan.ForGroup(group, new[] { alias });

        Assert.True(group.HasSharedCopies);
        Assert.NotEmpty(h.Gate.Needs(plan).SharedPhotos);
        Assert.Contains(h.Gate.Needs(plan).CloudServices, c => c.Service == "OneDrive");
        Assert.False(h.Gate.Check(plan, new Confirmations()).Allowed);
    }

    [Fact]
    public void Newly_shared_folders_are_rechecked_before_the_removal_action()
    {
        using var h = new RemovalHarness("sharing-fresh", new NoBinRules());
        h.Identical(911, "a.jpg", "b.jpg");
        var sharedNow = false;
        var shared = new SharedFolderDetector(new Func<string, string?>[] { d => sharedNow && d == h.Ws.Pictures ? "shared now" : null }, () => Array.Empty<string>());
        var gate = new SafetyGate(h.Ws.Fs, shared, h.Cloud);
        var group = Assert.Single(h.Scan().Decided);
        var plan = h.PlanFor(group);
        Assert.Empty(gate.Needs(plan).SharedPhotos);
        sharedNow = true;

        var needs = gate.Needs(plan);

        Assert.NotEmpty(needs.SharedPhotos);
        Assert.False(gate.Check(plan, new Confirmations()).Allowed);
    }
}
