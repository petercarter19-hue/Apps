using System.Windows;
using ImageMagick;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Made-up same-photo groups: the same picture saved at full size and smaller, with or without its date.</summary>
internal static class SameData
{
    public static Meta Dated(int i) => new($"2026:10:{1 + i % 27:00} 14:{i % 60:00}:09", "07", "Apple", "iPhone 15 Pro", Gps: true);

    /// <summary>
    /// <c>pic.jpg</c> (full size) and <c>pic-small.jpg</c> (smaller), plus an identical copy of pic.jpg and/or a third, even smaller version.
    /// With <paramref name="dated"/> false the small copy has all its hidden info stripped (like a chat-app copy), so the group has a missing date.
    /// </summary>
    public static string[] Group(TestWorkspace ws, int seed, string folder, bool dated = true, bool identicalCopy = false, bool third = false)
    {
        using var scene = TestScenes.Scene(seed);
        var meta = Dated(seed);
        var full = TestScenes.Encode(scene, MagickFormat.Jpeg, 92, meta);
        var paths = new List<string> { ws.Photo($"{folder}\\pic.jpg", full) };
        if (identicalCopy) paths.Add(ws.Photo($"{folder}\\pic (1).jpg", full));
        paths.Add(ws.Photo($"{folder}\\pic-small.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 80, dated ? meta : null, 50)));
        if (third) paths.Add(ws.Photo($"{folder}\\pic-tiny.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 70, dated ? meta : null, 70)));
        return paths.ToArray();
    }

    public static string Where(string folder) => $"\\{folder}\\";
}

/// <summary>A question that stays open until the test answers it (to look at the screen while Pete is being asked).</summary>
internal sealed class HoldingDialogs : IDialogs
{
    public TaskCompletionSource<DialogChoice> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<DialogChoice> ConfirmAsync(ConfirmRequest request) { Asked.TrySetResult(); return Answer.Task; }
    public void ShowMessage(string title, string message) { }
}

/// <summary>"Remove all same-photo copies": the button, the question, the removal, Undo, and Pete's whole run from his real computer with made-up photos.</summary>
[Collection("WPF")]
public class BulkSamePhotoTests
{
    private const string WhereTheyGo = "Removed photos go to the Recycle Bin, or to a \"Duplicates to delete\" folder on drives that don't have one. You can undo it from the Undo list.";
    private const string LeftOutOne = "1 group with missing date info is left for you to check one by one.";

    private static GroupVm SameIn(Step3Env e, string folder) => e.Vm.SameGroups.Single(g => g.Model.Files.Any(f => f.Path.Contains(SameData.Where(folder))));
    private static GroupVm ExactIn(Step3Env e, string folder) => e.Vm.ExactGroups.Single(g => g.Model.Files.Any(f => f.Path.Contains(SameData.Where(folder))));
    private static string[] Order(IEnumerable<GroupVm> groups) => groups.Select(g => g.Model.Key).ToArray();
    private static string[] Paths(GroupVm g) => g.Model.Files.Select(f => f.Path).ToArray();
    private static List<string> ExtrasOf(IEnumerable<GroupVm> groups) => groups.SelectMany(g => g.Model.Others.Select(o => o.Path)).ToList();
    private static long SizeOf(IEnumerable<string> paths) => paths.Sum(p => new FileInfo(p).Length);
    private static string ButtonText(int photos, long bytes) => $"Remove all same-photo copies ({Format.Plural(photos, "photo", "photos")}, {Format.Size(bytes)})";
    private static string ExactButtonText(int photos, long bytes) => $"Remove all exact copies ({Format.Plural(photos, "photo", "photos")}, {Format.Size(bytes)})";

    private static Dictionary<string, FileSnapshot> SnapAll(string root) =>
        Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Where(f => !f.Contains("Duplicates to delete"))
            .ToDictionary(f => f, FileSnapshot.Of, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, (string Sha, long Size)> Contents(Dictionary<string, FileSnapshot> s) => s.ToDictionary(kv => kv.Key, kv => (kv.Value.Sha, kv.Value.Size), StringComparer.OrdinalIgnoreCase);

    private static UndoViewModel NewUndo(Step3Env e) => WpfHost.Run(() => new UndoViewModel(e.S, e.Vm));

    private static void AssertButtonOffered(Step3Env e, int photos, long bytes)
    {
        Assert.Equal(ButtonText(photos, bytes), e.Vm.RemoveAllSamePhotoText);
        Assert.Equal(Visibility.Visible, e.Vm.RemoveAllSamePhotoVisibility);
        Assert.True(e.Vm.RemoveAllSamePhotoCommand.CanExecute(null));
    }

    private static void AssertButtonHidden(Step3Env e)
    {
        Assert.Equal(Visibility.Collapsed, e.Vm.RemoveAllSamePhotoVisibility);
        Assert.False(e.Vm.RemoveAllSamePhotoCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ the button

    [Fact]
    public async Task The_button_counts_only_same_photo_groups_with_dates_and_says_how_many_photos_and_how_much_space()
    {
        using var e = new Step3Env("bs-count");
        SameData.Group(e.Ws, 1, "S0", third: true);                    // 3 photos: 2 extras
        SameData.Group(e.Ws, 2, "S1");                                 // 2 photos: 1 extra
        SameData.Group(e.Ws, 3, "S2", dated: false);                   // a photo with no date: left for Pete to check by eye
        e.Identical(50, "E\\a.jpg", "E\\b.jpg");                       // exact copies belong to the other button
        await e.ScanAsync();

        Assert.Equal(3, e.Vm.SameGroups.Count);
        var s0 = SameIn(e, "S0"); var s1 = SameIn(e, "S1"); var s2 = SameIn(e, "S2");
        Assert.Equal(Visibility.Collapsed, s0.NoDateWarningVisibility);
        Assert.Equal(Visibility.Visible, s2.NoDateWarningVisibility);
        Assert.Equal("pic.jpg", s0.Model.Keeper.Name);
        var extras = ExtrasOf(new[] { s0, s1 });
        Assert.Equal(3, extras.Count);
        AssertButtonOffered(e, 3, SizeOf(extras));
        Assert.Equal(ExactButtonText(1, SizeOf(ExtrasOf(e.Vm.ExactGroups))), e.Vm.RemoveAllExactText);   // the exact button counts only exact copies
        Assert.Equal(Visibility.Visible, e.Vm.RemoveAllExactVisibility);
    }

    [Fact]
    public async Task The_button_is_hidden_when_there_is_nothing_to_remove_in_bulk()
    {
        using var e = new Step3Env("bs-hidden");
        AssertButtonHidden(e);                                         // before any scan
        await e.RemoveAllSamePhotoAsync();                             // asked directly: nothing happens
        Assert.Empty(e.Dialogs.Requests);

        SameData.Group(e.Ws, 5, "N0", dated: false);                   // the only same-photo group has a missing date
        e.Identical(51, "E\\a.jpg", "E\\b.jpg");
        await e.ScanAsync();
        Assert.Single(e.Vm.SameGroups);
        AssertButtonHidden(e);
        Assert.Equal(Visibility.Visible, e.Vm.RemoveAllExactVisibility);
        var before = SnapAll(e.Ws.Pictures);
        await e.RemoveAllSamePhotoAsync();
        Assert.Empty(e.Dialogs.Requests);                              // no question, no removal
        Assert.Equal(before.Keys.OrderBy(k => k), SnapAll(e.Ws.Pictures).Keys.OrderBy(k => k));
        Assert.Empty(e.S.Journal.Entries());
    }

    [Fact]
    public async Task The_button_is_hidden_when_there_are_exact_copies_only()
    {
        using var e = new Step3Env("bs-exactonly");
        e.Identical(52, "E\\a.jpg", "E\\b.jpg");
        await e.ScanAsync();
        Assert.Empty(e.Vm.SameGroups);
        AssertButtonHidden(e);
    }

    [Fact]
    public async Task Removing_groups_one_by_one_keeps_the_button_count_right_and_hides_it_after_the_last()
    {
        using var e = new Step3Env("bs-onebyone");
        SameData.Group(e.Ws, 1, "S0", third: true);
        SameData.Group(e.Ws, 2, "S1");
        SameData.Group(e.Ws, 3, "N0", dated: false);
        await e.ScanAsync();
        var s0 = SameIn(e, "S0"); var s1 = SameIn(e, "S1");
        AssertButtonOffered(e, 3, SizeOf(ExtrasOf(new[] { s0, s1 })));

        await e.RemoveAsync(s0);                                       // one group by hand: no question, as before
        Assert.Empty(e.Dialogs.Requests);
        AssertButtonOffered(e, 1, SizeOf(ExtrasOf(new[] { s1 })));

        await e.RemoveAsync(s1);
        AssertButtonHidden(e);
        Assert.Single(e.Vm.SameGroups);                                // the missing-date group is still there to check by eye
        Assert.Equal($"1 duplicate found. You can free {Format.Size(SizeOf(ExtrasOf(e.Vm.SameGroups)))}.", e.Vm.Headline);
    }

    [Fact]
    public async Task Moving_the_star_changes_what_the_button_says_it_will_remove()
    {
        using var e = new Step3Env("bs-star-count");
        SameData.Group(e.Ws, 1, "S0");
        await e.ScanAsync();
        var g = SameIn(e, "S0");
        var before = e.Vm.RemoveAllSamePhotoText;

        WpfHost.Run(() => e.Vm.SetKeeper(g, g.Cards.Single(c => c.Name == "pic-small.jpg")));   // keep the small one: the big one now goes

        Assert.Equal("pic-small.jpg", g.Model.Keeper.Name);
        Assert.NotEqual(before, e.Vm.RemoveAllSamePhotoText);
        AssertButtonOffered(e, 1, new FileInfo(g.Model.Others.Single().Path).Length);
    }

    [Fact]
    public async Task The_button_is_off_while_removing_and_back_to_normal_afterwards()
    {
        using var e = new Step3Env("bs-busy", new NoBinRules { DelayMs = 250 });
        for (int i = 0; i < 6; i++) SameData.Group(e.Ws, 10 + i, $"S{i}");
        await e.ScanAsync();
        var text = e.Vm.RemoveAllSamePhotoText;
        e.Dialogs.Answer(DialogChoice.Primary);

        var removing = WpfHost.Run(() => e.Vm.RemoveAllSamePhotoAsync());
        Assert.True(SpinWait.SpinUntil(() => e.Vm.StatusText.StartsWith("Removing") && e.Vm.ProgressValue >= 1, 30000));

        Assert.True(e.Vm.IsBusy);
        Assert.False(e.Vm.RemoveAllSamePhotoCommand.CanExecute(null));       // off while it works
        Assert.False(e.Vm.RemoveAllExactCommand.CanExecute(null));
        Assert.False(e.Vm.ScanCommand.CanExecute(null));
        Assert.False(e.Vm.SameGroups[0].RemoveCommand.CanExecute(null));
        Assert.Equal(Visibility.Visible, e.Vm.RemoveAllSamePhotoVisibility); // still shown, just not clickable
        Assert.Equal(text, e.Vm.RemoveAllSamePhotoText);
        await e.RemoveAllSamePhotoAsync();                                   // a second click while busy does nothing
        Assert.Single(e.Dialogs.Requests);

        await removing;

        Assert.False(e.Vm.IsBusy);
        Assert.StartsWith("Removed 6 photos", e.Vm.ResultText);
        AssertButtonHidden(e);
        Assert.True(e.Vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_button_is_off_while_the_question_is_open_and_comes_back_when_Pete_says_no()
    {
        using var e = new Step3Env("bs-question-open");
        SameData.Group(e.Ws, 1, "S0");
        SameData.Group(e.Ws, 2, "S1");
        var hold = new HoldingDialogs();
        var vm = WpfHost.Run(() => new MainViewModel(e.S, hold));
        await WpfHost.Run(() => vm.StartScanAsync());
        var before = SnapAll(e.Ws.Pictures);
        Assert.True(vm.RemoveAllSamePhotoCommand.CanExecute(null));

        var asking = WpfHost.Run(() => vm.RemoveAllSamePhotoAsync());
        await hold.Asked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(vm.IsBusy);
        Assert.False(vm.RemoveAllSamePhotoCommand.CanExecute(null));
        Assert.False(vm.RemoveAllExactCommand.CanExecute(null));
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.SameGroups[0].RemoveCommand.CanExecute(null));
        Assert.False(vm.CanUndo);

        hold.Answer.SetResult(DialogChoice.Cancel);
        await asking;

        Assert.False(vm.IsBusy);
        Assert.True(vm.RemoveAllSamePhotoCommand.CanExecute(null));
        Assert.True(vm.SameGroups[0].RemoveCommand.CanExecute(null));
        Assert.Equal("Nothing was removed.", vm.ResultText);
        Assert.Equal(before.Keys.OrderBy(k => k), SnapAll(e.Ws.Pictures).Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task While_a_scan_is_running_the_button_is_gone_and_a_direct_call_does_nothing()
    {
        using var e = new Step3Env("bs-scanning");
        SameData.Group(e.Ws, 1, "S0");
        await e.ScanAsync();
        AssertButtonOffered(e, 1, SizeOf(ExtrasOf(e.Vm.SameGroups)));
        var gate = new ManualResetEventSlim();
        e.Ws.Fs.BeforeOpen = _ => gate.Wait(TimeSpan.FromSeconds(30));
        var scanning = WpfHost.Run(() => e.Vm.StartScanAsync());
        Assert.True(SpinWait.SpinUntil(() => e.Vm.IsScanning, 10000));

        AssertButtonHidden(e);
        await e.RemoveAllSamePhotoAsync();
        Assert.Empty(e.Dialogs.Requests);
        Assert.Equal(2, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());

        gate.Set();
        await scanning;
        AssertButtonOffered(e, 1, SizeOf(ExtrasOf(e.Vm.SameGroups)));        // and it is back once the scan has finished
    }

    // ------------------------------------------------------------------ the question

    [Fact]
    public async Task Cancel_removes_nothing_and_the_question_is_worded_as_Pete_asked_with_Cancel_as_the_default()
    {
        using var e = new Step3Env("bs-cancel");
        SameData.Group(e.Ws, 1, "S0", third: true);
        SameData.Group(e.Ws, 2, "S1");
        SameData.Group(e.Ws, 3, "N0", dated: false);
        await e.ScanAsync();
        var extras = ExtrasOf(new[] { SameIn(e, "S0"), SameIn(e, "S1") });
        var before = SnapAll(e.Ws.Pictures);
        var text = e.Vm.RemoveAllSamePhotoText;
        e.Dialogs.Answer(DialogChoice.Cancel);

        await e.RemoveAllSamePhotoAsync();

        var ask = Assert.Single(e.Dialogs.Requests);
        Assert.Equal("Remove all same-photo copies?", ask.Title);
        Assert.Equal($"Remove 3 copies ({Format.Size(SizeOf(extras))})? These are the same picture saved in different ways, not identical files. Each group keeps the photo with the star. {LeftOutOne}", ask.Message);
        Assert.Equal("Remove 3 photos", ask.PrimaryText);
        Assert.Null(ask.SecondaryText);
        Assert.Equal("Cancel", ask.CancelText);
        Assert.Equal(DialogChoice.Cancel, ask.Default);                      // Enter does not remove
        Assert.Equal(WhereTheyGo, ask.Detail);
        Assert.Equal(before, SnapAll(e.Ws.Pictures));                        // every photo exactly as it was, none moved
        Assert.False(Directory.Exists(Path.Combine(e.Ws.Pictures, "Duplicates to delete")));
        Assert.Empty(e.S.Journal.Entries());
        Assert.Equal("Nothing was removed.", e.Vm.ResultText);
        Assert.False(e.Vm.IsBusy);
        Assert.Equal(text, e.Vm.RemoveAllSamePhotoText);
        Assert.True(e.Vm.RemoveAllSamePhotoCommand.CanExecute(null));
        Assert.Equal(3, e.Vm.SameGroups.Count);
    }

    [Fact]
    public async Task A_single_group_is_still_asked_first_and_the_wording_says_one_copy()
    {
        using var e = new Step3Env("bs-single");
        SameData.Group(e.Ws, 1, "S0");
        await e.ScanAsync();
        var g = SameIn(e, "S0");
        var extra = g.Model.Others.Single().Path;
        e.Dialogs.Answer(DialogChoice.Cancel, DialogChoice.Primary);

        await e.RemoveAllSamePhotoAsync();

        var ask = Assert.Single(e.Dialogs.Requests);
        Assert.Equal($"Remove 1 copy ({Format.Size(SizeOf(new[] { extra }))})? These are the same picture saved in different ways, not identical files. Each group keeps the photo with the star.", ask.Message);
        Assert.Equal("Remove 1 photo", ask.PrimaryText);
        Assert.DoesNotContain("missing date", ask.Message);
        Assert.True(File.Exists(extra));

        await e.RemoveAllSamePhotoAsync();

        Assert.Equal(2, e.Dialogs.Requests.Count);
        Assert.False(File.Exists(extra));
        Assert.True(File.Exists(g.Model.Keeper.Path));
        Assert.StartsWith("Removed 1 photo ", e.Vm.ResultText);
    }

    [Fact]
    public async Task The_left_out_line_appears_only_when_groups_were_left_out_and_counts_them()
    {
        // nothing left out
        using (var e = new Step3Env("bs-leftout-0"))
        {
            SameData.Group(e.Ws, 1, "S0"); SameData.Group(e.Ws, 2, "S1");
            await e.ScanAsync();
            await e.RemoveAllSamePhotoAsync();
            Assert.DoesNotContain("missing date", e.Dialogs.Requests.Single().Message);
            Assert.EndsWith("Each group keeps the photo with the star.", e.Dialogs.Requests.Single().Message);
        }
        // one group left out
        using (var e = new Step3Env("bs-leftout-1"))
        {
            SameData.Group(e.Ws, 1, "S0"); SameData.Group(e.Ws, 3, "N0", dated: false);
            await e.ScanAsync();
            await e.RemoveAllSamePhotoAsync();
            Assert.EndsWith("Each group keeps the photo with the star. " + LeftOutOne, e.Dialogs.Requests.Single().Message);
        }
        // two groups left out
        using (var e = new Step3Env("bs-leftout-2"))
        {
            SameData.Group(e.Ws, 1, "S0"); SameData.Group(e.Ws, 3, "N0", dated: false); SameData.Group(e.Ws, 4, "N1", dated: false);
            await e.ScanAsync();
            await e.RemoveAllSamePhotoAsync();
            Assert.EndsWith("Each group keeps the photo with the star. 2 groups with missing date info are left for you to check one by one.", e.Dialogs.Requests.Single().Message);
        }
    }

    [Fact]
    public async Task Yes_removes_every_extra_leaves_every_keeper_untouched_and_the_missing_date_group_alone()
    {
        using var e = new Step3Env("bs-yes");
        SameData.Group(e.Ws, 1, "S0", identicalCopy: true);            // 3 photos, two of them identical
        SameData.Group(e.Ws, 2, "S1");
        SameData.Group(e.Ws, 3, "S2", third: true);
        SameData.Group(e.Ws, 4, "N0", dated: false);
        await e.ScanAsync();
        var removable = new[] { SameIn(e, "S0"), SameIn(e, "S1"), SameIn(e, "S2") };
        var extras = ExtrasOf(removable);
        Assert.Equal(5, extras.Count);
        long extrasBytes = SizeOf(extras);
        var keepers = removable.Select(g => g.Model.Keeper.Path).ToList();
        var keepersBefore = keepers.ToDictionary(k => k, FileSnapshot.Of);
        var n0 = SameIn(e, "N0");
        var n0Before = Paths(n0).ToDictionary(p => p, FileSnapshot.Of);
        var n0Extra = SizeOf(ExtrasOf(new[] { n0 }));
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAllSamePhotoAsync();

        Assert.Single(e.Dialogs.Requests);
        Assert.All(extras, p => Assert.False(File.Exists(p), p));            // every extra is gone from its place ...
        Assert.Equal(5, Directory.EnumerateFiles(Path.Combine(e.Ws.Pictures), "*.jpg", SearchOption.AllDirectories).Count(f => f.Contains("Duplicates to delete")));   // ... into the holding folder (this drive, as pretended, has no Recycle Bin)
        foreach (var k in keepers) Assert.Equal(keepersBefore[k], FileSnapshot.Of(k));   // byte for byte, time for time
        foreach (var (p, snap) in n0Before) Assert.Equal(snap, FileSnapshot.Of(p));       // the missing-date group was not touched
        Assert.StartsWith($"Removed 5 photos ({Format.Size(extrasBytes)}).", e.Vm.ResultText);
        Assert.Contains("You can put them back from Undo.", e.Vm.ResultText);
        Assert.False(e.Vm.ResultIsProblem);
        Assert.Equal(5, e.S.Journal.Entries().Count(x => x.Status == EntryStatus.Done));
        var left = Assert.Single(e.Vm.SameGroups);                           // only the missing-date group is still listed
        Assert.Equal(n0.Model.Key, left.Model.Key);
        Assert.Equal($"1 duplicate found. You can free {Format.Size(n0Extra)}.", e.Vm.Headline);
        AssertButtonHidden(e);                                               // nothing removable is left
        Assert.True(e.Vm.CanChangeThings);
        Assert.False(e.Vm.IsBusy);
        Assert.Equal("Same photo (1)", e.Vm.SameTabHeader);
    }

    [Fact]
    public async Task The_photo_Pete_gave_the_star_is_the_one_that_stays()
    {
        using var e = new Step3Env("bs-chosen");
        SameData.Group(e.Ws, 1, "S0", third: true);
        await e.ScanAsync();
        var g = SameIn(e, "S0");
        var small = g.Cards.Single(c => c.Name == "pic-small.jpg");
        WpfHost.Run(() => e.Vm.SetKeeper(g, small));
        var smallBefore = FileSnapshot.Of(small.File.Path);
        var others = g.Model.Others.Select(o => o.Path).ToList();
        Assert.Equal(2, others.Count);
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAllSamePhotoAsync();

        Assert.Equal(smallBefore, FileSnapshot.Of(small.File.Path));
        Assert.All(others, p => Assert.False(File.Exists(p)));
        Assert.Contains("pic.jpg", e.S.Journal.Entries().Select(x => Path.GetFileName(x.OriginalPath)));
    }

    [Fact]
    public async Task Everything_is_refused_in_plain_words_if_a_photo_to_keep_vanished_after_the_scan()
    {
        using var e = new Step3Env("bs-keeper-gone");
        SameData.Group(e.Ws, 1, "S0");
        SameData.Group(e.Ws, 2, "S1");
        await e.ScanAsync();
        var s0 = SameIn(e, "S0"); var s1 = SameIn(e, "S1");
        File.Delete(s0.Model.Keeper.Path);                                   // behind the app's back
        var before = SnapAll(e.Ws.Pictures);
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAllSamePhotoAsync();

        Assert.StartsWith("PhotoTwins did not remove anything.", e.Vm.ResultText);
        Assert.Contains("photo to keep", e.Vm.ResultText);
        Assert.True(e.Vm.ResultIsProblem);
        Assert.Equal(before.Keys.OrderBy(k => k), SnapAll(e.Ws.Pictures).Keys.OrderBy(k => k));   // not even the other group lost a photo
        Assert.True(File.Exists(s1.Model.Others.Single().Path));
        Assert.Empty(e.S.Journal.Entries());
        Assert.False(e.Vm.IsBusy);
    }

    [Fact]
    public async Task A_photo_that_changed_after_the_scan_is_left_out_and_listed_and_the_rest_still_go()
    {
        using var e = new Step3Env("bs-changed");
        SameData.Group(e.Ws, 1, "S0", third: true);
        SameData.Group(e.Ws, 2, "S1");
        await e.ScanAsync();
        var s0 = SameIn(e, "S0");
        var edited = s0.Model.Others.First().Path;
        File.AppendAllText(edited, "edited later");
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAllSamePhotoAsync();

        Assert.Contains("Removed 2 photos", e.Vm.ResultText);
        Assert.Contains("1 photo was left out because it changed or disappeared after the scan", e.Vm.ResultText);
        Assert.True(e.Vm.ResultIsProblem);
        Assert.Equal(Visibility.Visible, e.Vm.ResultDetailsVisibility);
        Assert.True(File.Exists(edited));
        Assert.True(File.Exists(s0.Model.Keeper.Path));
    }

    [Fact]
    public async Task The_Recycle_Bin_route_works_and_Undo_puts_the_photo_back_into_its_group()
    {
        using var e = new Step3Env("bs-realbin", realBin: true);
        SameData.Group(e.Ws, 1, "S0");
        await e.ScanAsync();
        var g = SameIn(e, "S0");
        var extra = g.Model.Others.Single().Path;
        var keeperBefore = FileSnapshot.Of(g.Model.Keeper.Path);
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAllSamePhotoAsync();

        Assert.False(File.Exists(extra));
        Assert.Equal(keeperBefore, FileSnapshot.Of(g.Model.Keeper.Path));
        Assert.Equal(RemoveMethod.Recycle, e.S.Journal.Entries().Single(x => x.Status == EntryStatus.Done).Method);
        Assert.Contains("Recycle Bin", e.Vm.ResultText);
        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));
        Assert.True(File.Exists(extra));
        Assert.Equal(2, e.Vm.SameGroups.Single().Model.Files.Count);
        AssertButtonOffered(e, 1, SizeOf(new[] { extra }));
    }

    // ------------------------------------------------------------------ shared folders and the cloud are still asked

    [Fact]
    public async Task A_shared_folder_photo_gets_its_own_second_question_inside_the_bulk_action()
    {
        using var e = new Step3Env("bs-shared");
        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "Family"));
        using (var scene = TestScenes.Scene(20))
        {
            var meta = SameData.Dated(20);
            e.Ws.Photo("Family\\pic.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, meta));                 // shared: it always stays
            e.Ws.Photo("Family\\pic-small.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 80, meta, 50));       // shared
            e.Ws.Photo("Mine\\pic-tiny.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 70, meta, 70));          // not shared
        }
        SameData.Group(e.Ws, 21, "S1");
        await e.ScanAsync();
        var family = SameIn(e, "Family");
        Assert.Equal("pic.jpg", family.Model.Keeper.Name);
        Assert.True(family.Model.IsShared(family.Model.Keeper));
        var sharedExtra = Path.Combine(e.Ws.Pictures, "Family", "pic-small.jpg");
        var mineExtra = Path.Combine(e.Ws.Pictures, "Mine", "pic-tiny.jpg");
        var other = SameIn(e, "S1").Model.Others.Single().Path;
        var keeper = FileSnapshot.Of(family.Model.Keeper.Path);

        // Pete says no to the shared question: nothing at all is removed
        e.Dialogs.Answer(DialogChoice.Primary, DialogChoice.Cancel);
        await e.RemoveAllSamePhotoAsync();
        Assert.Equal(new[] { "Remove all same-photo copies?", "Photos in a shared folder" }, e.Dialogs.Requests.Select(r => r.Title).ToArray());
        var shared = e.Dialogs.Requests[1];
        Assert.Equal("1 of these photos is in a shared folder (\"Family\"). Other people who use that folder will lose it too. Remove it as well?", shared.Message);
        Assert.Equal(DialogChoice.Secondary, shared.Default);                // the safe answer is the default
        Assert.Equal("Nothing was removed.", e.Vm.ResultText);
        Assert.All(new[] { sharedExtra, mineExtra, other }, p => Assert.True(File.Exists(p), p));

        // "Skip shared photos": everything else goes, the shared photo stays
        e.Dialogs.Answer(DialogChoice.Primary, DialogChoice.Secondary);
        await e.RemoveAllSamePhotoAsync();
        Assert.Equal(4, e.Dialogs.Requests.Count);
        Assert.True(File.Exists(sharedExtra));
        Assert.False(File.Exists(mineExtra));
        Assert.False(File.Exists(other));
        Assert.StartsWith("Removed 2 photos", e.Vm.ResultText);
        Assert.Equal(keeper, FileSnapshot.Of(family.Model.Keeper.Path));

        // what is left is the two shared photos: removing the shared copy too needs the question again
        Assert.Equal(2, family.Model.Files.Count);
        e.Dialogs.Answer(DialogChoice.Primary, DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        Assert.Equal(6, e.Dialogs.Requests.Count);
        Assert.False(File.Exists(sharedExtra));
        Assert.Equal(keeper, FileSnapshot.Of(family.Model.Keeper.Path));     // the shared keeper is still byte for byte the same
        Assert.StartsWith("Removed 1 photo ", e.Vm.ResultText);
    }

    [Fact]
    public async Task A_cloud_folder_gets_its_one_line_question_once_for_the_whole_bulk_action()
    {
        using var e = new Step3Env("bs-cloud", cloudRelative: new[] { "OneDrive" });
        SameData.Group(e.Ws, 1, "OneDrive\\A");
        SameData.Group(e.Ws, 2, "OneDrive\\B");
        SameData.Group(e.Ws, 3, "Plain");
        await e.ScanAsync();
        var extras = ExtrasOf(e.Vm.SameGroups);
        Assert.Equal(3, extras.Count);

        e.Dialogs.Answer(DialogChoice.Primary, DialogChoice.Cancel);
        await e.RemoveAllSamePhotoAsync();
        Assert.Equal(new[] { "Remove all same-photo copies?", "Photos in the cloud" }, e.Dialogs.Requests.Select(r => r.Title).ToArray());
        Assert.Equal("These photos are in OneDrive. Removing them here also removes them from OneDrive online and from your other devices. Remove them?", e.Dialogs.Requests[1].Message);
        Assert.Equal(DialogChoice.Cancel, e.Dialogs.Requests[1].Default);
        Assert.All(extras, p => Assert.True(File.Exists(p), p));
        Assert.Equal("Nothing was removed.", e.Vm.ResultText);

        e.Dialogs.Answer(DialogChoice.Primary, DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        Assert.Equal(4, e.Dialogs.Requests.Count);                           // asked once for this action, not once per group
        Assert.All(extras, p => Assert.False(File.Exists(p), p));
        Assert.StartsWith("Removed 3 photos", e.Vm.ResultText);
    }

    [Fact]
    public async Task All_three_questions_come_in_the_right_order_when_all_three_apply()
    {
        using var e = new Step3Env("bs-order", cloudRelative: new[] { "OneDrive" });
        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "OneDrive", "Shared"));
        SameData.Group(e.Ws, 1, "OneDrive\\Shared");
        await e.ScanAsync();
        e.Dialogs.Answer(DialogChoice.Primary, DialogChoice.Primary, DialogChoice.Primary);

        await e.RemoveAllSamePhotoAsync();

        Assert.Equal(new[] { "Remove all same-photo copies?", "Photos in a shared folder", "Photos in the cloud" }, e.Dialogs.Requests.Select(r => r.Title).ToArray());
        Assert.StartsWith("Removed 1 photo ", e.Vm.ResultText);
    }

    // ------------------------------------------------------------------ Undo

    [Fact]
    public async Task Put_back_all_puts_every_photo_back_into_its_group_in_its_place_with_its_star_and_it_can_be_removed_again()
    {
        using var e = new Step3Env("bs-undo-all");
        SameData.Group(e.Ws, 1, "S0", identicalCopy: true);
        SameData.Group(e.Ws, 2, "S1");
        SameData.Group(e.Ws, 3, "S2", third: true);
        SameData.Group(e.Ws, 4, "N0", dated: false);
        await e.ScanAsync();
        var order = Order(e.Vm.SameGroups);
        var paths = e.Vm.SameGroups.ToDictionary(g => g.Model.Key, Paths);
        var keepers = e.Vm.SameGroups.ToDictionary(g => g.Model.Key, g => g.Model.Keeper.Path);
        var headline = e.Vm.Headline;
        var text = e.Vm.RemoveAllSamePhotoText;
        var contents = Contents(SnapAll(e.Ws.Pictures));
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        Assert.Single(e.Vm.SameGroups);
        Assert.NotEqual(headline, e.Vm.Headline);

        var undo = NewUndo(e);
        var action = Assert.Single(undo.Actions);                            // one action in the Undo list, however many groups
        Assert.Equal(5, action.AllRows.Count);
        Assert.Contains("5 photos removed", action.Header);
        await WpfHost.Run(() => undo.PutBackAllAsync(action.AllRows));

        Assert.Equal(order, Order(e.Vm.SameGroups));                         // same groups, same order
        foreach (var g in e.Vm.SameGroups)
        {
            Assert.Equal(paths[g.Model.Key], Paths(g));                      // each photo in its own place in the group
            Assert.Equal(keepers[g.Model.Key], g.Model.Keeper.Path);         // the star where it was
            Assert.Equal(paths[g.Model.Key].Length, g.Cards.Count);
            Assert.Equal(g.Model.Keeper.Path, g.Cards[0].File.Path);
            Assert.Single(g.Cards, c => c.IsKeeper);
        }
        var s0 = SameIn(e, "S0");
        Assert.Equal(2, s0.Cards.Count(c => c.Badge.Contains("Identical copy")));    // the two identical files are marked again
        Assert.Equal(headline, e.Vm.Headline);
        AssertButtonOffered(e, 5, SizeOf(ExtrasOf(e.Vm.SameGroups.Where(g => !g.Model.SomeHaveNoDateInfo))));
        Assert.Equal(text, e.Vm.RemoveAllSamePhotoText);
        Assert.Equal("Put back 5 photos. They are back in their groups.", e.Vm.ResultText);
        Assert.False(e.Vm.ResultIsProblem);
        Assert.Contains("They are back in their groups on the main screen", undo.Message);
        Assert.Equal(contents, Contents(SnapAll(e.Ws.Pictures)));            // every photo is back, byte for byte

        // the restored photos pass the safety check again: a second bulk removal works
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        Assert.StartsWith("Removed 5 photos", e.Vm.ResultText);
        Assert.DoesNotContain("left out", e.Vm.ResultText);
    }

    [Fact]
    public async Task The_star_Pete_chose_comes_back_where_it_was_after_Undo()
    {
        using var e = new Step3Env("bs-undo-star");
        SameData.Group(e.Ws, 1, "S0", third: true);
        await e.ScanAsync();
        var g = SameIn(e, "S0");
        WpfHost.Run(() => e.Vm.SetKeeper(g, g.Cards.Single(c => c.Name == "pic-small.jpg")));
        var starPath = g.Model.Keeper.Path;
        var filesBefore = Paths(g);
        Assert.True(g.Model.KeeperChosenByUser);
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        Assert.Empty(e.Vm.SameGroups);

        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));

        var back = e.Vm.SameGroups.Single();
        Assert.Equal(starPath, back.Model.Keeper.Path);
        Assert.True(back.Model.KeeperChosenByUser);
        Assert.Equal("★ Keeper (your choice)", back.Cards.Single(c => c.IsKeeper).KeeperExplanation);
        Assert.Equal(filesBefore, Paths(back));
        Assert.Equal(starPath, back.Cards[0].File.Path);
    }

    [Fact]
    public async Task Photos_can_be_put_back_one_by_one_and_the_button_follows_each_step()
    {
        using var e = new Step3Env("bs-undo-one");
        SameData.Group(e.Ws, 1, "S0", third: true);
        await e.ScanAsync();
        var g = SameIn(e, "S0");
        var order = Paths(g);
        var text = e.Vm.RemoveAllSamePhotoText;
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        AssertButtonHidden(e);

        var undo = NewUndo(e);
        var rows = undo.Actions.Single().Rows;
        Assert.Equal(2, rows.Count);
        await WpfHost.Run(() => undo.PutBackAsync(rows[0].Entry));

        var half = e.Vm.SameGroups.Single();                                 // the keeper and one photo: a group again
        Assert.Equal(2, half.Model.Files.Count);
        AssertButtonOffered(e, 1, new FileInfo(half.Model.Others.Single().Path).Length);

        await WpfHost.Run(() => undo.PutBackAsync(rows[1].Entry));

        Assert.Equal(order, Paths(e.Vm.SameGroups.Single()));
        Assert.Equal(text, e.Vm.RemoveAllSamePhotoText);
    }

    [Fact]
    public async Task A_missing_date_group_is_still_removed_one_by_one_and_Undo_never_makes_it_part_of_the_bulk_button()
    {
        using var e = new Step3Env("bs-undo-nodate");
        SameData.Group(e.Ws, 1, "S0");
        SameData.Group(e.Ws, 2, "N0", dated: false);
        await e.ScanAsync();
        var n0 = SameIn(e, "N0");
        var text = e.Vm.RemoveAllSamePhotoText;
        var order = Paths(n0);

        await e.RemoveAsync(n0);                                             // by hand, as before: no question
        Assert.Empty(e.Dialogs.Requests);
        Assert.Equal(text, e.Vm.RemoveAllSamePhotoText);
        Assert.Single(e.Vm.SameGroups);

        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));

        Assert.Equal(2, e.Vm.SameGroups.Count);
        Assert.Equal(order, Paths(SameIn(e, "N0")));
        Assert.Equal(Visibility.Visible, SameIn(e, "N0").NoDateWarningVisibility);
        Assert.Equal(text, e.Vm.RemoveAllSamePhotoText);                     // still only the group with dates
    }

    // ------------------------------------------------------------------ Pete's own sequence, end to end

    [Fact]
    public async Task Petes_whole_run_exact_copies_then_same_photo_copies_then_Undo_both_and_scan_again()
    {
        using var e = new Step3Env("bs-petes-run");
        // exact copies
        e.Identical(201, "E0\\a.jpg", "E0\\b.jpg", "E0\\c.jpg");
        e.Identical(202, "E1\\a.jpg", "E1\\b.jpg");
        e.Identical(203, "E2\\a.jpg", "E2\\b.jpg");
        // the same picture saved different ways, with dates (one group has an identical copy inside it) ...
        SameData.Group(e.Ws, 1, "S0", identicalCopy: true);
        SameData.Group(e.Ws, 2, "S1");
        SameData.Group(e.Ws, 3, "S2", third: true);
        // ... and one where a copy came back from a chat app without its date
        SameData.Group(e.Ws, 4, "N0", dated: false);
        var contents = Contents(SnapAll(e.Ws.Pictures));

        // 1. scan
        await e.ScanAsync();
        Assert.Equal(3, e.Vm.ExactGroups.Count);
        Assert.Equal(4, e.Vm.SameGroups.Count);
        var exactBytes = SizeOf(ExtrasOf(e.Vm.ExactGroups));
        var sameDated = e.Vm.SameGroups.Where(g => !g.Model.SomeHaveNoDateInfo).ToList();
        var sameBytes = SizeOf(ExtrasOf(sameDated));
        var n0Bytes = SizeOf(ExtrasOf(e.Vm.SameGroups.Where(g => g.Model.SomeHaveNoDateInfo)));
        var allBytes = exactBytes + sameBytes + n0Bytes;
        var order1 = Order(e.Vm.SameGroups);
        var keepers1 = e.Vm.ExactGroups.Concat(e.Vm.SameGroups).ToDictionary(g => g.Model.Key, g => g.Model.Keeper.Path);
        Assert.Equal($"10 duplicates found. You can free {Format.Size(allBytes)}.", e.Vm.Headline);
        Assert.Equal(ExactButtonText(4, exactBytes), e.Vm.RemoveAllExactText);
        AssertButtonOffered(e, 5, sameBytes);
        var headline1 = e.Vm.Headline;

        // 2. remove one group by hand (an exact group)
        await e.RemoveAsync(ExactIn(e, "E1"));
        Assert.Equal(2, e.Vm.ExactGroups.Count);
        var exactBytes2 = SizeOf(ExtrasOf(e.Vm.ExactGroups));
        Assert.Equal($"9 duplicates found. You can free {Format.Size(exactBytes2 + sameBytes + n0Bytes)}.", e.Vm.Headline);
        Assert.Equal(ExactButtonText(3, exactBytes2), e.Vm.RemoveAllExactText);
        AssertButtonOffered(e, 5, sameBytes);                                // the same-photo button did not move

        // 3. remove all exact copies
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllExactAsync();
        Assert.Equal("Remove all exact copies?", e.Dialogs.Requests.Single().Title);
        Assert.Empty(e.Vm.ExactGroups);
        Assert.Equal(Visibility.Collapsed, e.Vm.RemoveAllExactVisibility);
        Assert.Equal($"6 duplicates found. You can free {Format.Size(sameBytes + n0Bytes)}.", e.Vm.Headline);
        AssertButtonOffered(e, 5, sameBytes);

        // 4. scan again: no exact groups, the same-photo groups and their button are offered
        await e.ScanAsync();
        Assert.Empty(e.Vm.ExactGroups);
        Assert.Equal(4, e.Vm.SameGroups.Count);
        Assert.Equal("Exact copies (0)", e.Vm.ExactTabHeader);
        Assert.Equal("Same photo (4)", e.Vm.SameTabHeader);
        Assert.Equal(1, e.Vm.SelectedTab);                                   // the tab with the work opens
        Assert.Equal($"6 duplicates found. You can free {Format.Size(sameBytes + n0Bytes)}.", e.Vm.Headline);
        Assert.Equal(Visibility.Collapsed, e.Vm.RemoveAllExactVisibility);
        AssertButtonOffered(e, 5, sameBytes);
        var headline4 = e.Vm.Headline;
        var sameButton4 = e.Vm.RemoveAllSamePhotoText;
        var keepers4 = e.Vm.SameGroups.ToDictionary(g => g.Model.Key, g => g.Model.Keeper.Path);
        var keeperSnaps = keepers4.Values.ToDictionary(k => k, FileSnapshot.Of);
        var removableExtras = ExtrasOf(e.Vm.SameGroups.Where(g => !g.Model.SomeHaveNoDateInfo));
        var n0Snaps = Paths(SameIn(e, "N0")).ToDictionary(p => p, FileSnapshot.Of);

        // 5. remove all same-photo copies
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        Assert.Equal(2, e.Dialogs.Requests.Count);
        var ask = e.Dialogs.Requests[1];
        Assert.Equal("Remove all same-photo copies?", ask.Title);
        Assert.Equal($"Remove 5 copies ({Format.Size(sameBytes)})? These are the same picture saved in different ways, not identical files. Each group keeps the photo with the star. {LeftOutOne}", ask.Message);
        Assert.Equal(DialogChoice.Cancel, ask.Default);
        Assert.All(removableExtras, p => Assert.False(File.Exists(p), p));
        foreach (var (p, snap) in keeperSnaps) Assert.Equal(snap, FileSnapshot.Of(p));
        foreach (var (p, snap) in n0Snaps) Assert.Equal(snap, FileSnapshot.Of(p));
        Assert.StartsWith($"Removed 5 photos ({Format.Size(sameBytes)}).", e.Vm.ResultText);
        var sameLeft = Assert.Single(e.Vm.SameGroups);
        Assert.True(sameLeft.Model.SomeHaveNoDateInfo);
        Assert.Equal($"1 duplicate found. You can free {Format.Size(n0Bytes)}.", e.Vm.Headline);
        Assert.Equal("Same photo (1)", e.Vm.SameTabHeader);

        // 6. nothing removable is left: no button, and asking again changes nothing
        AssertButtonHidden(e);
        Assert.Equal(Visibility.Collapsed, e.Vm.RemoveAllExactVisibility);
        await e.RemoveAllSamePhotoAsync(); await e.RemoveAllExactAsync();
        Assert.Equal(2, e.Dialogs.Requests.Count);

        // 7. Undo "Put back all" for the same-photo action: it is on screen again; then the two exact-copy actions
        var undo = NewUndo(e);
        Assert.Equal(3, undo.Actions.Count);                                 // one action per click: one group, all exact, all same-photo
        var sameAction = undo.Actions.Single(a => a.AllRows.Any(r => r.Entry.OriginalPath.Contains(SameData.Where("S1"))));
        await WpfHost.Run(() => undo.PutBackAllAsync(sameAction.AllRows));
        Assert.Equal(order1, Order(e.Vm.SameGroups));
        Assert.Equal(headline4, e.Vm.Headline);
        Assert.Equal(sameButton4, e.Vm.RemoveAllSamePhotoText);
        AssertButtonOffered(e, 5, sameBytes);
        foreach (var g in e.Vm.SameGroups) Assert.Equal(keepers4[g.Model.Key], g.Model.Keeper.Path);
        Assert.Equal("Put back 5 photos. They are back in their groups.", e.Vm.ResultText);

        foreach (var a in undo.Actions.Where(a => a != sameAction))
            await WpfHost.Run(() => undo.PutBackAllAsync(a.AllRows));
        Assert.Contains("scan again", undo.Message);                          // the exact groups are not on this list any more: it says so plainly
        Assert.Contains("scan again", e.Vm.ResultText);
        Assert.Empty(e.S.Journal.Entries().Where(x => x.Status == EntryStatus.Done));   // everything is put back
        Assert.Equal(contents, Contents(SnapAll(e.Ws.Pictures)));            // every photo back, byte for byte

        // 8. scan again: the groups, the buttons and the headline are exactly what they were at the start
        await e.ScanAsync();
        Assert.Equal(3, e.Vm.ExactGroups.Count);
        Assert.Equal(4, e.Vm.SameGroups.Count);
        Assert.Equal(headline1, e.Vm.Headline);
        Assert.Equal(ExactButtonText(4, exactBytes), e.Vm.RemoveAllExactText);
        AssertButtonOffered(e, 5, sameBytes);
        Assert.Equal(order1, Order(e.Vm.SameGroups));
        foreach (var g in e.Vm.ExactGroups.Concat(e.Vm.SameGroups)) Assert.Equal(keepers1[g.Model.Key], g.Model.Keeper.Path);
    }

    // ------------------------------------------------------------------ the pictures

    [Fact]
    public async Task The_Same_photo_tab_with_its_button_and_the_question_fit_every_size_and_read_clearly()
    {
        using var e = new Step3Env("bs-pictures");
        SameData.Group(e.Ws, 1, "Holiday", identicalCopy: true);
        SameData.Group(e.Ws, 2, "Garden");
        SameData.Group(e.Ws, 3, "Party", third: true);
        SameData.Group(e.Ws, 4, "Chat", dated: false);
        await e.ScanAsync();
        e.Vm.SelectedTab = 1;
        ScreenShots.WaitForPictures(e.Vm.SameGroups, 4);
        ScreenShots.Take(e.Vm, "bulk-same", "same-tab-with-button", height: 1100);

        // the question, from the real run (one group left out) ...
        e.Dialogs.Answer(DialogChoice.Cancel);
        await e.RemoveAllSamePhotoAsync();
        ScreenShots.TakeConfirm(e.Dialogs.Requests.Single(), "bulk-same-question");
        // ... and a big one, the size of Pete's real run, to see the long wording wrap
        ScreenShots.TakeConfirm(new ConfirmRequest("Remove all same-photo copies?",
            "Remove 1,008 copies (3.2 GB)? These are the same picture saved in different ways, not identical files. Each group keeps the photo with the star. 37 groups with missing date info are left for you to check one by one.",
            "Remove 1,008 photos", null, "Cancel", DialogChoice.Cancel, WhereTheyGo), "bulk-same-question-big");

        // after the removal: the button is gone and only the group with the missing date is left
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllSamePhotoAsync();
        AssertButtonHidden(e);
        ScreenShots.WaitForPictures(e.Vm.SameGroups, 4);
        ScreenShots.Take(e.Vm, "bulk-same", "same-tab-after-removal", height: 900);
    }
}

/// <summary>The safety gate and the plan for "Remove all same-photo copies": crafted plans the app itself could never make are refused.</summary>
public class BulkSamePhotoGateTests
{
    private sealed class NoBinVolumes : VolumeRules
    {
        public override string? WhyNotRecyclable(string path, long fileSize) => "the test says this drive has no Recycle Bin";
    }

    private static readonly Confirmations Yes = new(BulkSamePhoto: true);

    private static RemovalHarness Harness(string name, Func<string, IEnumerable<string>>? cloud = null) => new(name, new NoBinVolumes(), cloud);
    private static DecidedGroup In(IEnumerable<DecidedGroup> groups, string folder) => groups.Single(g => g.Files.All(f => f.Path.Contains(SameData.Where(folder))));
    private static PlannedGroup Crafted(DecidedGroup g, PhotoFile keeper, params PhotoFile[] targets) => new(g, keeper, targets);
    private static PlannedGroup Whole(DecidedGroup g) => Crafted(g, g.Keeper, g.Others.ToArray());
    private static RemovalPlan Plan(RemovalHarness h, params PlannedGroup[] groups) => new(PlanKind.AllSamePhoto, groups, h.Roots);
    private static void AssertAllThere(IEnumerable<string> paths) => Assert.All(paths, p => Assert.True(File.Exists(p), p));
    private static string[] AllPaths(params DecidedGroup[] gs) => gs.SelectMany(g => g.Files.Select(f => f.Path)).ToArray();

    private static (RemovalHarness H, DecidedGroup S0, DecidedGroup S1) Two(string name)
    {
        var h = Harness(name);
        SameData.Group(h.Ws, 1, "S0", third: true);
        SameData.Group(h.Ws, 2, "S1");
        var decided = h.Scan().Decided;
        return (h, In(decided, "S0"), In(decided, "S1"));
    }

    // ------------------------------------------------------------ the confirmation flag

    [Fact]
    public void Without_its_own_confirmation_the_gate_refuses_and_nothing_moves()
    {
        var (h, s0, s1) = Two("bsg-noflag");
        using var _h = h;
        var plan = RemovalPlan.ForAllSamePhoto(new[] { s0, s1 }, h.Roots);
        Assert.Equal(PlanKind.AllSamePhoto, plan.Kind);
        Assert.Equal(3, plan.PhotoCount);
        Assert.True(h.Gate.Needs(plan).BulkSamePhoto);
        Assert.False(h.Gate.Needs(plan).BulkExactCopies);

        var none = h.Run(plan);
        var wrongOnes = h.Run(plan, new Confirmations(BulkExactCopies: true, SharedFolders: true, Cloud: true));   // the other answers don't count

        Assert.Contains("many same-photo copies at once needs your confirmation", none.Refusal);
        Assert.Contains("many same-photo copies at once needs your confirmation", wrongOnes.Refusal);
        Assert.Empty(none.Removed); Assert.Empty(wrongOnes.Removed);
        AssertAllThere(AllPaths(s0, s1));
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void With_the_confirmation_only_the_extras_go_and_every_keeper_is_untouched()
    {
        var (h, s0, s1) = Two("bsg-flag");
        using var _h = h;
        var keepers = new[] { s0.Keeper.Path, s1.Keeper.Path }.ToDictionary(k => k, RemovalHarness.Snap);

        var r = h.Run(RemovalPlan.ForAllSamePhoto(new[] { s0, s1 }, h.Roots), Yes);

        Assert.Null(r.Refusal);
        Assert.Equal(3, r.Removed.Count);
        Assert.All(s0.Others.Concat(s1.Others), f => Assert.False(File.Exists(f.Path)));
        foreach (var (k, snap) in keepers) Assert.Equal(snap, RemovalHarness.Snap(k));
        Assert.Equal(3, h.Journal.Entries().Count(x => x.Status == EntryStatus.Done));
    }

    [Fact]
    public void The_exact_copies_confirmation_does_not_open_the_exact_route_to_same_photo_plans_and_the_other_way_round()
    {
        var (h, s0, s1) = Two("bsg-twoflags");
        using var _h = h;
        h.Identical(9, "E\\a.jpg", "E\\b.jpg");
        var exact = In(h.Scan().Decided, "E");
        var exactPlan = RemovalPlan.ForAllExactCopies(new[] { exact }, h.Roots);

        Assert.False(h.Gate.Needs(exactPlan).BulkSamePhoto);
        Assert.Contains("many exact copies at once needs your confirmation", h.Run(exactPlan, Yes).Refusal);   // the same-photo answer is not an exact-copies answer
        Assert.Empty(h.Journal.Entries());
        AssertAllThere(AllPaths(exact, s0, s1));
    }

    // ------------------------------------------------------------ what a same-photo plan may hold

    [Fact]
    public void The_plan_maker_refuses_exact_look_alike_and_missing_date_groups_even_among_good_ones()
    {
        var (h, s0, s1) = Two("bsg-maker");
        using var _h = h;
        SameData.Group(h.Ws, 3, "N0", dated: false);
        h.Identical(9, "E\\a.jpg", "E\\b.jpg");
        var decided = h.Scan().Decided;
        var noDate = In(decided, "N0"); var exact = In(decided, "E");
        var lookAlike = new DecidedGroup(GroupKind.LookAlike, s1.Files, null, new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.True(noDate.SomeHaveNoDateInfo);

        var e1 = Assert.Throws<ArgumentException>(() => RemovalPlan.ForAllSamePhoto(new[] { s0, exact }, h.Roots));
        var e2 = Assert.Throws<ArgumentException>(() => RemovalPlan.ForAllSamePhoto(new[] { s0, lookAlike }, h.Roots));
        var e3 = Assert.Throws<ArgumentException>(() => RemovalPlan.ForAllSamePhoto(new[] { s0, s1, noDate }, h.Roots));
        Assert.Throws<ArgumentException>(() => RemovalPlan.ForAllSamePhoto(new[] { exact }, h.Roots));
        Assert.Throws<ArgumentException>(() => RemovalPlan.ForAllSamePhoto(new[] { noDate }, h.Roots));

        Assert.Contains("Only same-photo groups", e1.Message);
        Assert.Contains("Only same-photo groups", e2.Message);
        Assert.Contains("no date info", e3.Message);
        Assert.Equal(2, RemovalPlan.ForAllSamePhoto(new[] { s0, s1 }, h.Roots).Groups.Count);   // while the good ones are fine
    }

    [Fact]
    public void The_gate_refuses_a_same_photo_plan_that_holds_an_exact_group_and_nothing_at_all_moves()
    {
        var (h, s0, s1) = Two("bsg-exact-in-plan");
        using var _h = h;
        h.Identical(9, "E\\a.jpg", "E\\b.jpg");
        var exact = In(h.Scan().Decided, "E");

        var r = h.Run(Plan(h, Whole(s0), Whole(exact), Whole(s1)), Yes);

        Assert.Contains("same-photo groups only", r.Refusal);
        Assert.Empty(r.Removed);
        AssertAllThere(AllPaths(s0, s1, exact));                              // the good groups were not touched either
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void The_gate_refuses_a_same_photo_plan_that_holds_a_look_alike_group()
    {
        var (h, s0, s1) = Two("bsg-look-in-plan");
        using var _h = h;
        var lookAlike = new DecidedGroup(GroupKind.LookAlike, s1.Files, null, new Dictionary<string, string>(), new Dictionary<string, string>());

        var r = h.Run(Plan(h, Whole(s0), Whole(lookAlike)), Yes);

        Assert.Contains("same-photo groups only", r.Refusal);
        Assert.Empty(r.Removed);
        AssertAllThere(AllPaths(s0, s1));
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void The_gate_refuses_a_same_photo_plan_that_holds_a_group_with_a_missing_date()
    {
        var (h, s0, s1) = Two("bsg-nodate-in-plan");
        using var _h = h;
        SameData.Group(h.Ws, 3, "N0", dated: false);
        var noDate = In(h.Scan().Decided, "N0");

        var r = h.Run(Plan(h, Whole(s0), Whole(noDate)), Yes);

        Assert.Contains("no date info", r.Refusal);
        Assert.Empty(r.Removed);
        AssertAllThere(AllPaths(s0, s1, noDate));
        Assert.Empty(h.Journal.Entries());
    }

    // ------------------------------------------------------------ the same checks as for bulk exact copies

    [Fact]
    public void A_keeper_whose_contents_changed_even_with_the_same_size_and_time_is_found_by_the_re_read_and_everything_is_refused()
    {
        var (h, s0, s1) = Two("bsg-keeper-reread");
        using var _h = h;
        var path = s0.Keeper.Path;
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;                                      // one byte differs: same length ...
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);                                // ... and the same time, as far as a quick look can tell

        var r = h.Run(RemovalPlan.ForAllSamePhoto(new[] { s0, s1 }, h.Roots), Yes);

        Assert.Contains("no longer has the same contents", r.Refusal);
        Assert.Empty(r.Removed);
        AssertAllThere(AllPaths(s0, s1));
    }

    [Fact]
    public void A_keeper_that_vanished_or_was_replaced_by_another_file_refuses_the_whole_action()
    {
        var (h, s0, s1) = Two("bsg-keeper-gone");
        using var _h = h;
        File.Delete(s1.Keeper.Path);
        var gone = h.Run(RemovalPlan.ForAllSamePhoto(new[] { s0, s1 }, h.Roots), Yes);
        Assert.Contains("is no longer there", gone.Refusal);
        AssertAllThere(s0.Files.Select(f => f.Path));                         // the other group lost nothing either

        var (h2, t0, t1) = Two("bsg-keeper-replaced");
        using var _h2 = h2;
        var kp = t0.Keeper.Path; var stamp = File.GetLastWriteTimeUtc(kp); var bytes = File.ReadAllBytes(kp);
        File.Delete(kp); File.WriteAllBytes(kp, bytes); File.SetLastWriteTimeUtc(kp, stamp);   // a new file with the same name, size, time and contents
        var replaced = h2.Run(RemovalPlan.ForAllSamePhoto(new[] { t0, t1 }, h2.Roots), Yes);
        Assert.Contains("a different file than the scan saw", replaced.Refusal);
        AssertAllThere(AllPaths(t0, t1));
    }

    [Fact]
    public void Photos_that_changed_vanished_or_were_swapped_since_the_scan_are_left_out_and_listed_and_the_rest_still_go()
    {
        var (h, s0, s1) = Two("bsg-targets");
        using var _h = h;
        var changed = s0.Others.ElementAt(0).Path;
        var vanished = s0.Others.ElementAt(1).Path;
        File.AppendAllText(changed, "edited later");
        File.Delete(vanished);
        var good = s1.Others.Single().Path;

        var r = h.Run(RemovalPlan.ForAllSamePhoto(new[] { s0, s1 }, h.Roots), Yes);

        Assert.Null(r.Refusal);
        Assert.Equal(new[] { good }, r.Removed.Select(x => x.OriginalPath).ToArray());
        Assert.Equal(2, r.Dropped.Count);
        Assert.Contains(r.Dropped, d => d.File.Path == changed && d.Why.Contains("changed after the scan"));
        Assert.Contains(r.Dropped, d => d.File.Path == vanished && d.Why.Contains("already gone"));
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(s0.Keeper.Path));

        // a photo swapped for a different file with the same name, size and time (a new Windows file ID) is caught too
        var (h2, t0, t1) = Two("bsg-targets-swapped");
        using var _h2 = h2;
        var victim = t1.Others.Single().Path; var stamp = File.GetLastWriteTimeUtc(victim); var bytes = File.ReadAllBytes(victim);
        File.Delete(victim); File.WriteAllBytes(victim, bytes); File.SetLastWriteTimeUtc(victim, stamp);
        var r2 = h2.Run(RemovalPlan.ForAllSamePhoto(new[] { t0, t1 }, h2.Roots), Yes);
        Assert.Null(r2.Refusal);
        Assert.Contains(r2.Dropped, d => d.File.Path == victim && d.Why.Contains("different file"));
        Assert.True(File.Exists(victim));
        Assert.Equal(2, r2.Removed.Count);                                    // the other group's two extras went
    }

    [Fact]
    public void A_photo_outside_the_picked_folders_is_refused_judged_on_its_real_location()
    {
        var (h, s0, s1) = Two("bsg-outside");
        using var _h = h;
        var plan = new RemovalPlan(PlanKind.AllSamePhoto, new[] { Whole(s0), Whole(s1) }, new[] { h.Ws.Outside });   // Pete picked a different folder

        var r = h.Run(plan, Yes);

        Assert.Contains("not inside the folders you picked", r.Refusal);
        Assert.Empty(r.Removed);
        AssertAllThere(AllPaths(s0, s1));
    }

    [Fact]
    public void A_plan_that_lists_the_keeper_or_names_a_keeper_that_is_not_in_the_group_is_refused()
    {
        var (h, s0, s1) = Two("bsg-keeper-listed");
        using var _h = h;
        var stranger = s0.Others.First() with { Path = Path.Combine(h.Ws.Pictures, "not-in-group.jpg") };

        var listed = h.Run(Plan(h, Crafted(s0, s0.Keeper, s0.Keeper, s0.Others.First()), Whole(s1)), Yes);
        var notInGroup = h.Run(Plan(h, Crafted(s0, stranger, s0.Others.ToArray())), Yes);

        Assert.Contains("photo to keep was in the list", listed.Refusal);
        Assert.NotNull(notInGroup.Refusal);
        Assert.Empty(listed.Removed); Assert.Empty(notInGroup.Removed);
        AssertAllThere(AllPaths(s0, s1));
    }

    [Fact]
    public void A_photo_listed_in_two_groups_is_refused()
    {
        var (h, s0, s1) = Two("bsg-twice");
        using var _h = h;
        var victim = s1.Others.Single();

        var r = h.Run(Plan(h, Crafted(s1, s1.Keeper, victim), Crafted(s1, s1.Keeper, victim)), Yes);

        Assert.NotNull(r.Refusal);
        Assert.True(File.Exists(victim.Path));
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void A_cloud_folder_still_needs_its_own_confirmation_in_the_gate()
    {
        using var h = Harness("bsg-cloud", pics => new[] { Path.Combine(pics, "OneDriveStandIn") });
        SameData.Group(h.Ws, 1, "OneDriveStandIn");
        var g = In(h.Scan().Decided, "OneDriveStandIn");
        var plan = RemovalPlan.ForAllSamePhoto(new[] { g }, h.Roots);
        Assert.Single(h.Gate.Needs(plan).CloudServices);

        var refused = h.Run(plan, Yes);
        Assert.Contains("cloud folder needs your confirmation", refused.Refusal);
        Assert.True(File.Exists(g.Others.Single().Path));

        var ok = h.Run(plan, new Confirmations(BulkSamePhoto: true, Cloud: true));
        Assert.Null(ok.Refusal);
        Assert.False(File.Exists(g.Others.Single().Path));
    }

    [Fact]
    public void A_shared_folder_photo_still_needs_its_own_confirmation_in_the_gate()
    {
        using var h = Harness("bsg-shared");
        h.SharedMarked.Add(Path.Combine(h.Ws.Pictures, "Family"));
        SameData.Group(h.Ws, 1, "Family");
        var g = In(h.Scan().Decided, "Family");
        var plan = RemovalPlan.ForAllSamePhoto(new[] { g }, h.Roots);
        Assert.Single(h.Gate.Needs(plan).SharedPhotos);

        var refused = h.Run(plan, Yes);
        Assert.Contains("shared folder", refused.Refusal);
        Assert.True(File.Exists(g.Others.Single().Path));

        var ok = h.Run(plan, new Confirmations(BulkSamePhoto: true, SharedFolders: true));
        Assert.Null(ok.Refusal);
        Assert.False(File.Exists(g.Others.Single().Path));
        Assert.True(File.Exists(g.Keeper.Path));
    }

    [Fact]
    public void A_cancelled_removal_stops_cleanly_and_what_was_done_can_be_put_back()
    {
        var (h, s0, s1) = Two("bsg-cancel");
        using var _h = h;
        using var cts = new CancellationTokenSource();
        var progress = new PhotoTwins.Tests.Support.SyncProgress<RemovalProgress>(p => { if (p.Done >= 1) cts.Cancel(); });

        var r = h.Service.Execute(RemovalPlan.ForAllSamePhoto(new[] { s0, s1 }, h.Roots), Yes, progress, cts.Token);

        Assert.True(r.Cancelled);
        Assert.InRange(r.Removed.Count, 1, 2);
        Assert.Equal(3 - r.Removed.Count, s0.Others.Concat(s1.Others).Count(f => File.Exists(f.Path)));
        foreach (var entry in r.Removed) Assert.True(h.Mutations.PutBack(entry).Ok);
        AssertAllThere(AllPaths(s0, s1));
    }
}
