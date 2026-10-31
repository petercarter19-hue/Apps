using System.Reflection;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>
/// The rules that stand between a click and a removal. Most tests hand the gate plans that the app itself could never make
/// (keeper in the list, photos outside the picked folders...) to prove it refuses them anyway.
/// </summary>
public class GateTests
{
    private sealed class NoBin : VolumeRules
    {
        public override string? WhyNotRecyclable(string path, long fileSize) => "the test says this drive has no Recycle Bin";
    }

    private static RemovalHarness Harness(string name) => new(name, new NoBin());

    private static PlannedGroup Crafted(DecidedGroup g, PhotoFile keeper, params PhotoFile[] targets) => new(g, keeper, targets);

    private static RemovalPlan CraftedPlan(RemovalHarness h, PlanKind kind, params PlannedGroup[] groups) => new(kind, groups, h.Roots);

    private static void AssertNothingChanged(params string[] paths) => Assert.All(paths, p => Assert.True(File.Exists(p), p));

    // ------------------------------------------------------------ S-LAST: the keeper

    [Fact]
    public void S_LAST_a_plan_that_lists_the_keeper_is_refused_and_nothing_moves()
    {
        using var h = Harness("g-keeper");
        var paths = h.Identical(1, "a.jpg", "b.jpg");
        var g = h.Scan().Decided.Single();
        var plan = CraftedPlan(h, PlanKind.OneGroup, Crafted(g, g.Keeper, g.Keeper, g.Others.Single()));

        var r = h.Run(plan);

        Assert.Contains("photo to keep was in the list", r.Refusal);
        Assert.Empty(r.Removed);
        AssertNothingChanged(paths);
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void S_LAST_a_plan_whose_keeper_is_not_even_in_the_group_or_that_would_empty_it_is_refused()
    {
        using var h = Harness("g-nokeeper");
        var paths = h.Identical(2, "a.jpg", "b.jpg");
        var g = h.Scan().Decided.Single();
        var other = g.Others.Single();
        var stranger = other with { Path = Path.Combine(h.Ws.Pictures, "not-in-group.jpg") };

        var r1 = h.Run(CraftedPlan(h, PlanKind.OneGroup, Crafted(g, stranger, other)));
        var r2 = h.Run(CraftedPlan(h, PlanKind.OneGroup, Crafted(g, g.Keeper, other, stranger)));

        Assert.NotNull(r1.Refusal);
        Assert.NotNull(r2.Refusal);
        Assert.Empty(r1.Removed); Assert.Empty(r2.Removed);
        AssertNothingChanged(paths);
    }

    [Fact]
    public void S_LAST_if_the_photo_to_keep_has_been_deleted_since_the_scan_the_whole_action_is_refused()
    {
        using var h = Harness("g-keeper-gone");
        var paths = h.Identical(3, "a.jpg", "b.jpg", "c.jpg");
        var g = h.Scan().Decided.Single();
        File.Delete(g.Keeper.Path);   // the test removes its own test photo behind the app's back

        var r = h.Run(h.PlanFor(g));

        Assert.Contains("is no longer there", r.Refusal);
        Assert.Empty(r.Removed);
        AssertNothingChanged(g.Others.Select(o => o.Path).ToArray());
    }

    [Fact]
    public void S_LAST_if_the_photo_to_keep_was_edited_or_swapped_since_the_scan_the_whole_action_is_refused()
    {
        foreach (var how in new[] { "edited", "same size and time but different contents", "replaced by a copy with the same contents and times" })
        {
            using var h = Harness("g-keeper-changed");
            h.Identical(4, "a.jpg", "b.jpg");
            var g = h.Scan().Decided.Single();
            var k = g.Keeper;
            var bytes = File.ReadAllBytes(k.Path);
            var when = File.GetLastWriteTimeUtc(k.Path);
            if (how == "edited") { File.AppendAllText(k.Path, "more"); }
            else if (how.StartsWith("same size"))
            {
                var different = (byte[])bytes.Clone(); different[^1] ^= 0xFF; different[^2] ^= 0xFF;
                File.WriteAllBytes(k.Path, different); File.SetLastWriteTimeUtc(k.Path, when);
            }
            else { File.Delete(k.Path); File.WriteAllBytes(k.Path, bytes); File.SetLastWriteTimeUtc(k.Path, when); }

            var r = h.Run(h.PlanFor(g));

            Assert.True(r.Refusal != null && r.Refusal.Contains("photo to keep"), how + ": " + r.Refusal);
            Assert.Empty(r.Removed);
            Assert.True(File.Exists(g.Others.Single().Path), how);
        }
    }

    // ------------------------------------------------------------ photos that changed: left out, listed, the rest go ahead

    [Fact]
    public void A_photo_that_changed_or_vanished_after_the_scan_is_left_out_and_listed_while_the_others_are_removed()
    {
        using var h = Harness("g-dropped");
        h.Identical(5, "a.jpg", "b.jpg", "c.jpg", "d.jpg");
        var g = h.Scan().Decided.Single();
        var others = g.Others.ToList();
        File.AppendAllText(others[0].Path, "edited since the scan");
        File.Delete(others[1].Path);

        var r = h.Run(h.PlanFor(g));

        Assert.Null(r.Refusal);
        Assert.Single(r.Removed);
        Assert.Equal(others[2].Path, r.Removed[0].OriginalPath, ignoreCase: true);
        Assert.Equal(2, r.Dropped.Count);
        Assert.Contains(r.Dropped, d => d.File.Path == others[0].Path && d.Why.Contains("changed after the scan"));
        Assert.Contains(r.Dropped, d => d.File.Path == others[1].Path && d.Why.Contains("already gone"));
        Assert.True(File.Exists(others[0].Path));   // the edited one is untouched
    }

    [Fact]
    public void When_every_photo_to_remove_has_changed_nothing_is_removed_and_the_user_is_told_why()
    {
        using var h = Harness("g-alldropped");
        h.Identical(6, "a.jpg", "b.jpg");
        var g = h.Scan().Decided.Single();
        File.AppendAllText(g.Others.Single().Path, "changed");

        var r = h.Run(h.PlanFor(g));

        Assert.Contains("every photo had changed or gone", r.Refusal);
        Assert.Single(r.Dropped);
    }

    [Fact]
    public void An_exact_copy_that_is_no_longer_identical_even_with_the_same_size_and_time_is_not_removed()
    {
        using var h = Harness("g-hash");
        h.Identical(7, "a.jpg", "b.jpg");
        var g = h.Scan().Decided.Single();
        var victim = g.Others.Single();
        var bytes = File.ReadAllBytes(victim.Path); var when = File.GetLastWriteTimeUtc(victim.Path);
        bytes[^1] ^= 0xFF; bytes[^2] ^= 0xFF;
        File.WriteAllBytes(victim.Path, bytes); File.SetLastWriteTimeUtc(victim.Path, when);

        var r = h.Run(h.PlanFor(g));

        Assert.NotNull(r.Refusal);
        Assert.Contains(r.Dropped, d => d.Why.Contains("no longer identical"));
        Assert.True(File.Exists(victim.Path));
    }

    // ------------------------------------------------------------ S-INSIDE: only inside the picked folders

    [Fact]
    public void S_INSIDE_a_photo_outside_the_picked_folders_is_refused_even_if_a_plan_lists_it()
    {
        using var h = Harness("g-outside");
        h.Identical(8, "a.jpg", "b.jpg");
        var g = h.Scan().Decided.Single();
        var plan = new RemovalPlan(PlanKind.OneGroup, new[] { Crafted(g, g.Keeper, g.Others.Single()) }, new[] { h.Ws.Outside });   // The user picked a different folder

        var r = h.Run(plan);

        Assert.Contains("not inside the folders you picked", r.Refusal);
        AssertNothingChanged(g.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void S_INSIDE_a_path_that_goes_through_a_shortcut_link_to_outside_the_picked_folder_is_seen_through_and_refused()
    {
        using var h = Harness("g-junction");
        var keeperPath = h.Identical(9, "keep.jpg")[0];
        var outsideFile = TestPhotoFactory.Write(Path.Combine(h.Ws.Outside, "victim.jpg"), File.ReadAllBytes(keeperPath));
        h.Ws.MakeJunction(Path.Combine(h.Ws.Pictures, "shortcut"), h.Ws.Outside);
        var viaLink = Path.Combine(h.Ws.Pictures, "shortcut", "victim.jpg");
        // the plan names the outside photo by its path THROUGH the link, which looks like it is inside Pictures
        var keeperFile = ScanOne(h, keeperPath);
        var victimFile = ScanOne(h, outsideFile) with { Path = viaLink };
        var group = new DecidedGroup(GroupKind.Exact, new[] { keeperFile, victimFile }, null, new Dictionary<string, string>(), new Dictionary<string, string>());

        var r = h.Run(h.PlanFor(group));

        Assert.Contains("not inside the folders you picked", r.Refusal);
        Assert.True(File.Exists(outsideFile));
        Assert.True(File.Exists(keeperPath));
    }

    private static PhotoFile ScanOne(RemovalHarness h, string path)
    {
        using var open = h.Ws.Fs.OpenRead(path);
        var bytes = File.ReadAllBytes(path);
        return new PhotoFile(path, open.Info.Size, open.Info.LastWriteUtcTicks, System.Security.Cryptography.SHA256.HashData(bytes), open.Info.Identity);
    }

    [Fact]
    public void S_INSIDE_the_real_file_system_knows_a_junction_is_a_link_and_a_plain_folder_is_not()
    {
        using var ws = new TestWorkspace("g-islink");
        var link = Path.Combine(ws.Root, "linkdir");
        ws.MakeJunction(link, ws.Outside);
        var fs = new RealFileSystem();
        Assert.True(fs.IsLink(link));
        Assert.False(fs.IsLink(ws.Pictures));
        Assert.Equal(fs.GetRealPath(ws.Outside), fs.GetRealPath(link), ignoreCase: true);
    }

    // ------------------------------------------------------------ S-SHARED-KEEP and S-SHARED-CONFIRM

    private static RemovalHarness SharedHarness(string name, out string sharedFolder)
    {
        var h = Harness(name);
        sharedFolder = Path.Combine(h.Ws.Pictures, "Family Photos");
        h.SharedMarked.Add(sharedFolder);
        return h;
    }

    [Fact]
    public void S_SHARED_KEEP_a_plan_that_keeps_a_copy_outside_the_shared_folder_when_a_shared_copy_exists_is_refused()
    {
        using var h = SharedHarness("g-shared-keep", out var shared);
        var paths = h.Identical(10, "Family Photos\\a.jpg", "Mine\\b.jpg");
        var g = h.Scan().Decided.Single();
        var sharedFile = g.Files.Single(f => g.IsShared(f));
        var mine = g.Files.Single(f => !g.IsShared(f));
        Assert.Equal(sharedFile.Path, g.Keeper.Path);   // the rule already picked the shared copy

        var r = h.Run(CraftedPlan(h, PlanKind.OneGroup, Crafted(g, mine, sharedFile)), new Confirmations(SharedFolders: true));

        Assert.Contains("copy in the shared folder always stays", r.Refusal);
        AssertNothingChanged(paths);
    }

    [Fact]
    public void S_SHARED_CONFIRM_removing_photos_from_a_shared_folder_needs_the_second_confirm_and_skipping_them_works_without_it()
    {
        using var h = SharedHarness("g-shared-confirm", out var shared);
        h.Identical(11, "Family Photos\\a.jpg", "Family Photos\\b.jpg", "Mine\\c.jpg");
        var g = h.Scan().Decided.Single();
        Assert.True(g.IsShared(g.Keeper));
        var sharedOther = g.Others.Single(f => g.IsShared(f));
        var mine = g.Others.Single(f => !g.IsShared(f));
        var plan = h.PlanFor(g);

        // needs: the gate knows one shared photo is in the plan
        var needs = h.Gate.Needs(plan);
        Assert.Single(needs.SharedPhotos);
        Assert.Equal(sharedOther.Path, needs.SharedPhotos[0].Path);

        // 1. no second confirm: refused, nothing removed
        var refused = h.Run(plan);
        Assert.Contains("shared folder", refused.Refusal);
        Assert.True(File.Exists(sharedOther.Path) && File.Exists(mine.Path));

        // 2. "Skip the shared photos": only the unshared one goes
        var skipped = plan.Without((pg, t) => pg.Group.IsShared(t));
        var ok = h.Run(skipped);
        Assert.Null(ok.Refusal);
        Assert.Equal(mine.Path, Assert.Single(ok.Removed).OriginalPath, ignoreCase: true);
        Assert.True(File.Exists(sharedOther.Path) && File.Exists(g.Keeper.Path));
        Assert.False(File.Exists(mine.Path));
    }

    [Fact]
    public void S_SHARED_CONFIRM_with_the_second_confirm_given_the_shared_photo_is_removed_and_the_shared_keeper_stays()
    {
        using var h = SharedHarness("g-shared-yes", out var shared);
        h.Identical(12, "Family Photos\\a.jpg", "Family Photos\\b.jpg");
        var g = h.Scan().Decided.Single();
        var victim = g.Others.Single();
        var keeperBefore = FileSnapshot.Of(g.Keeper.Path);

        var r = h.Run(h.PlanFor(g), new Confirmations(SharedFolders: true));

        Assert.Null(r.Refusal);
        Assert.Single(r.Removed);
        Assert.False(File.Exists(victim.Path));
        Assert.Equal(keeperBefore, FileSnapshot.Of(g.Keeper.Path));
    }

    [Fact]
    public void S_SHARED_the_star_cannot_be_moved_off_a_shared_copy()
    {
        using var h = SharedHarness("g-shared-star", out var shared);
        h.Identical(13, "Family Photos\\a.jpg", "Mine\\b.jpg");
        var g = h.Scan().Decided.Single();
        var mine = g.Files.Single(f => !g.IsShared(f));

        Assert.False(g.TrySetKeeper(mine));
        Assert.True(g.IsShared(g.Keeper));
    }

    // ------------------------------------------------------------ S-CLOUD-WARN

    [Fact]
    public void S_CLOUD_WARN_removing_from_a_cloud_folder_needs_the_cloud_confirm_once_for_the_whole_action()
    {
        using var h = new RemovalHarness("g-cloud", new NoBin(), pics => new[] { Path.Combine(pics, "OneDriveStandIn") });
        h.Identical(14, "OneDriveStandIn\\a.jpg", "OneDriveStandIn\\b.jpg", "OneDriveStandIn\\c.jpg");
        var g = h.Scan().Decided.Single();
        var plan = h.PlanFor(g);
        var needs = h.Gate.Needs(plan);
        Assert.Equal(("OneDrive", 2), needs.CloudServices.Single());

        var refused = h.Run(plan);
        Assert.Contains("cloud folder", refused.Refusal);
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(h.Ws.Pictures, "OneDriveStandIn")).Count());

        var ok = h.Run(plan, new Confirmations(Cloud: true));   // one confirm covers both photos
        Assert.Null(ok.Refusal);
        Assert.Equal(2, ok.Removed.Count);
    }

    [Fact]
    public void S_CLOUD_WARN_photos_outside_cloud_folders_need_no_cloud_confirm()
    {
        using var h = new RemovalHarness("g-nocloud", new NoBin(), pics => new[] { Path.Combine(pics, "OneDriveStandIn") });
        h.Identical(15, "Plain\\a.jpg", "Plain\\b.jpg");
        var g = h.Scan().Decided.Single();
        Assert.Empty(h.Gate.Needs(h.PlanFor(g)).CloudServices);
        Assert.Null(h.Run(h.PlanFor(g)).Refusal);
    }

    // ------------------------------------------------------------ S-BULK and S-NOBULK-LOOK

    [Fact]
    public void S_BULK_removing_all_exact_copies_needs_the_summary_confirm_and_refuses_without_it()
    {
        using var h = Harness("g-bulk");
        for (int i = 0; i < 4; i++) h.Identical(300 + i, $"G{i}\\a.jpg", $"G{i}\\b.jpg");
        var scan = h.Scan();
        var plan = RemovalPlan.ForAllExactCopies(scan.Decided, h.Roots);
        Assert.True(h.Gate.Needs(plan).BulkExactCopies);
        Assert.Equal(4, plan.PhotoCount);

        var refused = h.Run(plan);

        Assert.Contains("many exact copies at once needs your confirmation", refused.Refusal);
        Assert.Empty(refused.Removed);
        Assert.Equal(8, Directory.EnumerateFiles(h.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void S_NOBULK_LOOK_the_exact_copies_route_refuses_same_photo_groups_and_there_is_no_way_to_ask_for_look_alikes_in_bulk()
    {
        using var h = Harness("g-nobulk");
        using var scene = TestScenes.Scene(1);
        h.Ws.Photo("a.jpg", TestScenes.Encode(scene, ImageMagick.MagickFormat.Jpeg, 92));
        h.Ws.Photo("b.jpg", TestScenes.Encode(scene, ImageMagick.MagickFormat.Jpeg, 60, percent: 50));
        var scan = h.Scan();
        var same = scan.Decided.Single();
        Assert.Equal(GroupKind.Same, same.Kind);

        var ex = Assert.Throws<ArgumentException>(() => RemovalPlan.ForAllExactCopies(new[] { same }, h.Roots));
        Assert.Contains("Only exact copies", ex.Message);

        // and the only ways to make a plan are these three (all exact copies, all same-photo copies, one group): none takes look-alikes in bulk
        var makers = typeof(RemovalPlan).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "ForAllExactCopies", "ForAllSamePhoto", "ForGroup" }, makers);
        Assert.Empty(typeof(RemovalPlan).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    // ------------------------------------------------------------ other odd plans

    [Fact]
    public void A_photo_listed_twice_or_in_two_groups_or_an_empty_plan_is_refused()
    {
        using var h = Harness("g-odd");
        h.Identical(16, "a.jpg", "b.jpg");
        h.Identical(17, "c.jpg", "d.jpg");
        var gs = h.Scan().Decided;
        var g1 = gs[0]; var g2 = gs[1];
        var victim = g1.Others.Single();

        var twice = h.Run(CraftedPlan(h, PlanKind.OneGroup, Crafted(g1, g1.Keeper, victim, victim)));
        var inTwo = h.Run(CraftedPlan(h, PlanKind.AllExactCopies, Crafted(g1, g1.Keeper, victim), Crafted(g1, g1.Keeper, victim)), new Confirmations(BulkExactCopies: true));
        var empty = h.Run(CraftedPlan(h, PlanKind.OneGroup, Crafted(g2, g2.Keeper)));

        Assert.NotNull(twice.Refusal); Assert.NotNull(inTwo.Refusal); Assert.NotNull(empty.Refusal);
        Assert.True(File.Exists(victim.Path));
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void A_moved_star_decides_which_photo_stays_and_the_other_goes()
    {
        using var h = Harness("g-star");
        h.Identical(18, "a.jpg", "b.jpg");
        var g = h.Scan().Decided.Single();
        var first = g.Keeper;
        var second = g.Others.Single();

        Assert.True(g.TrySetKeeper(second));
        var r = h.Run(h.PlanFor(g));

        Assert.Null(r.Refusal);
        Assert.True(File.Exists(second.Path));
        Assert.False(File.Exists(first.Path));
        Assert.True(g.KeeperChosenByUser);
    }
}
