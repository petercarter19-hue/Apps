using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Photos put back by Undo reappear in the app: same group, same tab, same spot, same star, no rescan.</summary>
[Collection("WPF")]
public class UndoBackInAppTests
{
    private static UndoViewModel NewUndo(Step3Env e) => WpfHost.Run(() => new UndoViewModel(e.S, e.Vm));

    private static string[] Order(Step3Env e) => e.Vm.ExactGroups.Select(g => g.Model.Key).ToArray();
    private static string[] Paths(GroupVm g) => g.Model.Files.Select(f => f.Path).ToArray();

    // ------------------------------------------------------------ a single group, one photo at a time

    [Fact]
    public async Task Single_photos_put_back_one_by_one_reappear_in_their_group_in_their_place_with_the_headline_updated()
    {
        using var e = new Step3Env("ub-single");
        e.Identical(1, "G0\\a.jpg", "G0\\b.jpg", "G0\\c.jpg");
        e.Identical(2, "G1\\a.jpg", "G1\\b.jpg");
        e.Identical(3, "G2\\a.jpg", "G2\\b.jpg");
        await e.ScanAsync();
        var order = Order(e);
        var headlineBefore = e.Vm.Headline;
        var group = e.Vm.ExactGroups.Single(g => g.Model.Files.Count == 3);
        var key = group.Model.Key;
        var pathsBefore = Paths(group);
        var keeper = group.Model.Keeper.Path;
        var position = Array.IndexOf(order, key);

        await e.RemoveAsync(group);
        Assert.DoesNotContain(key, Order(e));                               // the group left the list
        Assert.NotEqual(headlineBefore, e.Vm.Headline);

        var undo = NewUndo(e);
        var rows = undo.Actions.Single().Rows;
        await WpfHost.Run(() => undo.PutBackAsync(rows[0].Entry));

        var back = e.Vm.ExactGroups.Single(g => g.Model.Key == key);       // it is on the Exact copies tab again ...
        Assert.Equal(position, e.Vm.ExactGroups.ToList().IndexOf(back));    // ... in the same spot
        Assert.Equal(2, back.Model.Files.Count);
        Assert.Equal(keeper, back.Model.Keeper.Path);                       // the star is where it was
        Assert.Contains("back in its group", undo.Message);
        Assert.StartsWith("Put back 1 photo.", e.Vm.ResultText);
        Assert.NotEqual(headlineBefore, e.Vm.Headline);                     // one duplicate is back in the total ...

        await WpfHost.Run(() => undo.PutBackAsync(rows[1].Entry));

        Assert.Equal(order, Order(e));                                      // ... and with both back everything is as before the removal
        Assert.Equal(pathsBefore, Paths(back));                             // same photos in the same order
        Assert.Equal(headlineBefore, e.Vm.Headline);
        Assert.Equal(keeper, back.Model.Keeper.Path);
        Assert.Equal(3, back.Cards.Count);
        Assert.True(File.Exists(pathsBefore[1]) && File.Exists(pathsBefore[2]));
    }

    // ------------------------------------------------------------ everything in one action

    [Fact]
    public async Task Put_back_all_restores_every_group_in_its_original_order_and_the_headline_is_what_it_was()
    {
        using var e = new Step3Env("ub-all");
        for (int i = 0; i < 5; i++) e.Identical(10 + i, $"G{i}\\a.jpg", $"G{i}\\b.jpg", $"G{i}\\c.jpg");
        await e.ScanAsync();
        var order = Order(e);
        var headline = e.Vm.Headline;
        var bulkText = e.Vm.RemoveAllExactText;
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAllExactAsync();
        Assert.Empty(e.Vm.ExactGroups);
        Assert.Equal("No duplicates left.", e.Vm.Headline);

        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));

        Assert.Equal(order, Order(e));
        Assert.Equal(headline, e.Vm.Headline);
        Assert.Equal(bulkText, e.Vm.RemoveAllExactText);                    // "Remove all exact copies" is offered again
        Assert.All(e.Vm.ExactGroups, g => Assert.Equal(3, g.Model.Files.Count));
        Assert.Contains("They are back in their groups on the main screen", undo.Message);
        Assert.Equal("Put back 10 photos. They are back in their groups.", e.Vm.ResultText);
        Assert.False(e.Vm.ResultIsProblem);
    }

    // ------------------------------------------------------------ mixed: several groups, only some put back, on two tabs

    [Fact]
    public async Task Mixed_groups_each_photo_goes_back_to_its_own_group_and_tab_and_the_rest_stay_removed()
    {
        using var e = new Step3Env("ub-mixed");
        e.Identical(20, "G0\\a.jpg", "G0\\b.jpg");
        e.Identical(21, "G1\\a.jpg", "G1\\b.jpg");
        e.Identical(22, "G2\\a.jpg", "G2\\b.jpg");
        // a same-photo group (the same picture saved two ways) lives on the other tab
        using (var scene = TestScenes.Scene(23))
        {
            e.Ws.Photo("S\\pic.jpg", TestScenes.Encode(scene, ImageMagick.MagickFormat.Jpeg, 92));
            e.Ws.Photo("S\\pic-small.jpg", TestScenes.Encode(scene, ImageMagick.MagickFormat.Jpeg, 80, null, 70));
        }
        await e.ScanAsync();
        var exactOrder = Order(e);
        var sameGroup = Assert.Single(e.Vm.SameGroups);
        var sameKey = sameGroup.Model.Key;
        var sameBefore = Paths(sameGroup);
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllExactAsync();
        await e.RemoveAsync(sameGroup);
        Assert.Empty(e.Vm.ExactGroups);
        Assert.Empty(e.Vm.SameGroups);

        var undo = NewUndo(e);
        var allRows = undo.Actions.SelectMany(a => a.AllRows).ToList();
        var middle = allRows.Single(r => r.Entry.OriginalPath.Contains(@"G1\"));
        var same = allRows.Single(r => r.Entry.OriginalPath.Contains(@"S\"));
        await WpfHost.Run(() => undo.PutBackAsync(middle.Entry));
        await WpfHost.Run(() => undo.PutBackAsync(same.Entry));

        Assert.Single(e.Vm.ExactGroups);                                    // only the middle group is back on the Exact tab ...
        Assert.Equal(exactOrder[1], e.Vm.ExactGroups[0].Model.Key);
        Assert.Single(e.Vm.SameGroups);                                     // ... and the same-photo group on its own tab
        Assert.Equal(sameKey, e.Vm.SameGroups[0].Model.Key);
        Assert.Equal(sameBefore, Paths(e.Vm.SameGroups[0]));
        Assert.True(File.Exists(middle.Entry.OriginalPath));
        Assert.False(File.Exists(allRows.First(r => r.Entry.OriginalPath.Contains(@"G0\")).Entry.OriginalPath));   // the others stay removed

        // and now the rest, in any order: every group ends up in its original spot
        var rest = allRows.Where(r => r.CanPutBack).ToList();
        await WpfHost.Run(() => undo.PutBackAllAsync(rest));
        Assert.Equal(exactOrder, Order(e));
    }

    // ------------------------------------------------------------ the star

    [Fact]
    public async Task The_star_stays_where_Pete_put_it_after_the_photos_come_back()
    {
        using var e = new Step3Env("ub-star");
        e.Identical(30, "a.jpg", "b.jpg", "c.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var chosen = g.Cards.First(c => !c.IsKeeper);
        WpfHost.Run(() => chosen.MakeKeeperCommand.Execute(null));          // The user moves the star to another photo
        var starPath = g.Model.Keeper.Path;
        Assert.True(g.Model.KeeperChosenByUser);

        await e.RemoveAsync(g);
        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));

        var back = e.Vm.ExactGroups.Single();
        Assert.Equal(starPath, back.Model.Keeper.Path);
        Assert.True(back.Model.KeeperChosenByUser);
        Assert.Equal("★ Keeper (your choice)", back.Cards.Single(c => c.IsKeeper).KeeperExplanation);
        Assert.Equal(starPath, back.Cards[0].File.Path);                    // shown first, as before
    }

    [Fact]
    public void Model_a_keeper_that_a_removal_moved_goes_back_to_being_the_keeper_and_an_emptied_group_comes_back_whole()
    {
        using var ws = new TestWorkspace("ub-model");
        var bytes = TestPhotoFactory.StandIn(40, 5000);
        var paths = new[] { "a.jpg", "b.jpg", "c.jpg" }.Select(n => ws.Photo(n, bytes)).ToArray();
        var group = Assert.Single(ws.Scan().ExactGroups);
        var decided = GroupDecider.Decide(new MatchResult { ExactGroups = new[] { group }, SameGroups = Array.Empty<SameGroup>() }, null, null, null).Single();
        var originalKeeper = decided.Keeper.Path;
        var original = decided.Files.Select(f => f.Path).ToArray();

        decided.RemoveFiles(new[] { originalKeeper });                      // another group's removal took this one's keeper
        Assert.NotEqual(originalKeeper, decided.Keeper.Path);
        Assert.Equal(2, decided.Files.Count);
        var restored = decided.RestoreFiles(new[] { originalKeeper });

        Assert.Equal(new[] { originalKeeper }, restored);
        Assert.Equal(originalKeeper, decided.Keeper.Path);                  // the star went back with it
        Assert.Equal(original, decided.Files.Select(f => f.Path).ToArray());

        decided.RemoveFiles(original);                                      // everything taken out: the group is empty ...
        Assert.Empty(decided.Files);
        var all = decided.RestoreFiles(original);                           // ... and comes back whole
        Assert.Equal(3, all.Count);
        Assert.Equal(original, decided.Files.Select(f => f.Path).ToArray());
        Assert.Contains(decided.Files, f => f.Path == decided.Keeper.Path);
        Assert.Empty(decided.RestoreFiles(original));                       // nothing more to put back: asking again changes nothing
        Assert.Equal(3, decided.Files.Count);
    }

    // ------------------------------------------------------------ after a rescan

    [Fact]
    public async Task After_a_rescan_a_photo_put_back_says_plainly_where_it_went_and_the_next_scan_groups_it_again()
    {
        using var e = new Step3Env("ub-rescan");
        e.Identical(50, "a.jpg", "b.jpg");
        e.Identical(51, "k.jpg", "l.jpg");
        await e.ScanAsync();
        var group = e.Vm.ExactGroups.Single(g => g.Cards.Any(c => c.Name == "a.jpg"));
        await e.RemoveAsync(group);
        await e.ScanAsync();                                                // The user scans again: the removed copy is not in this result
        var listBefore = Order(e);
        Assert.Single(listBefore);

        var undo = NewUndo(e);
        var row = undo.Actions.Single().Rows.Single();
        await WpfHost.Run(() => undo.PutBackAsync(row.Entry));

        Assert.True(File.Exists(row.Entry.OriginalPath));                   // it is back on disk
        Assert.Equal(listBefore, Order(e));                                 // the list on screen (from the newer scan) did not change
        Assert.Contains("aren't on its list any more", undo.Message);
        Assert.Contains("scan again", undo.Message);
        Assert.Contains("scan again", e.Vm.ResultText);
        Assert.False(e.Vm.ResultIsProblem);

        await e.ScanAsync();                                                // the next scan finds both copies and groups them again
        Assert.Equal(2, e.Vm.ExactGroups.Count);
    }

    [Fact]
    public async Task Putting_a_photo_back_after_it_was_already_put_back_changes_nothing_on_screen()
    {
        using var e = new Step3Env("ub-twice");
        e.Identical(60, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        await e.RemoveAsync(g);
        var undo = NewUndo(e);
        var row = undo.Actions.Single().Rows.Single();
        await WpfHost.Run(() => undo.PutBackAsync(row.Entry));
        var snapshot = Paths(e.Vm.ExactGroups.Single());

        await WpfHost.Run(() => undo.PutBackAsync(row.Entry));              // the second click: it was already put back

        Assert.Equal(snapshot, Paths(e.Vm.ExactGroups.Single()));
        Assert.Equal(2, e.Vm.ExactGroups.Single().Model.Files.Count);
    }

    // ------------------------------------------------------------ both routes, and removing again afterwards

    [Fact]
    public async Task The_Duplicates_to_delete_folder_route_comes_back_into_the_group_and_can_be_removed_again()
    {
        using var e = new Step3Env("ub-folder");                            // this drive (as pretended) has no Recycle Bin
        e.Identical(70, "T\\a.jpg", "T\\b.jpg", "T\\c.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        await e.RemoveAsync(g);
        var undo = NewUndo(e);
        Assert.All(undo.Actions.Single().Rows, r => Assert.Contains("Duplicates to delete", r.WhereText));
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));
        Assert.Equal(3, e.Vm.ExactGroups.Single().Model.Files.Count);

        await e.RemoveAsync(e.Vm.ExactGroups.Single());                     // a second removal works on the restored photos (they are still "the same file")

        Assert.Equal(2, e.S.Journal.Entries().Count(x => x.Status == EntryStatus.Done && x.Method == RemoveMethod.Folder));
        Assert.Contains("Removed 2 photos", e.Vm.ResultText);
        Assert.Empty(e.Vm.ExactGroups);
    }

    [Fact]
    public async Task The_Recycle_Bin_route_comes_back_into_the_group_and_can_be_removed_again()
    {
        using var e = new Step3Env("ub-bin", realBin: true);
        e.Identical(71, "a.jpg", "b.jpg", "c.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));
        Assert.Equal(3, e.Vm.ExactGroups.Single().Model.Files.Count);

        await e.RemoveAsync(e.Vm.ExactGroups.Single());

        Assert.Contains("Removed 2 photos", e.Vm.ResultText);                // not "left out because they changed": the restored files passed the safety check
        Assert.DoesNotContain("left out", e.Vm.ResultText);
    }

    // ------------------------------------------------------------ the picture of it

    [Fact]
    public async Task The_restored_group_is_drawn_on_the_screen_with_its_pictures_and_nothing_cut_off()
    {
        using var e = new Step3Env("ub-picture");
        e.Identical(80, "a.jpg", "b.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));

        ScreenShots.WaitForPictures(e.Vm.ExactGroups);
        ScreenShots.Take(e.Vm, "step5", "after-put-back");
    }
}
