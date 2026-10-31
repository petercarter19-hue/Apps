using System.Windows;
using ImageMagick;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Settings;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Step 3 on screen: the star, Remove, the questions asked first, what is said afterwards, and the Undo list. The questions are scripted; the removals are real.</summary>
[Collection("WPF")]
public class Step3UiTests
{
    private static readonly Meta Mine = new("2026:10:12 14:03:09", "07", "Apple", "iPhone 15 Pro", Gps: true);

    private static GroupVm ExactWith(Step3Env e, string fileName) => e.Vm.ExactGroups.Single(g => g.Model.Files.Any(f => f.Name == fileName));

    // -------------------------------------------------- Remove the others (one group): the click is the permission

    [Fact]
    public async Task Remove_the_others_on_one_group_removes_them_without_extra_questions_updates_the_screen_and_Undo_puts_them_back()
    {
        using var e = new Step3Env("s3-one");
        var paths = e.Identical(1, "Trip\\a.jpg", "Trip\\b.jpg", "Trip\\c.jpg");
        var other = e.Identical(2, "Other\\x.jpg", "Other\\y.jpg");
        long sz = new FileInfo(paths[0]).Length, sz2 = new FileInfo(other[0]).Length;
        await e.ScanAsync();
        Assert.Equal($"3 duplicates found. You can free {Format.Size(2 * sz + sz2)}.", e.Vm.Headline);
        var g = ExactWith(e, "a.jpg");
        Assert.Equal("Remove the others (2 photos, " + Format.Size(2 * sz) + ")", g.RemoveButtonText);
        var keeper = g.Model.Keeper;
        var keeperBefore = FileSnapshot.Of(keeper.Path);
        var gKey = g.Model.Key;

        await e.RemoveAsync(g);

        Assert.Empty(e.Dialogs.Requests);                                   // one group: the click itself is the permission
        Assert.Equal(keeperBefore, FileSnapshot.Of(keeper.Path));           // the keeper is untouched
        Assert.Single(Directory.EnumerateFiles(Path.Combine(e.Ws.Pictures, "Trip"), "*.jpg"));
        Assert.StartsWith($"Removed 2 photos ({Format.Size(2 * sz)}).", e.Vm.ResultText);
        Assert.Contains("moved to a \"Duplicates to delete\" folder", e.Vm.ResultText);   // this drive (as pretended) has no Recycle Bin
        Assert.Contains("You can put them back from Undo.", e.Vm.ResultText);
        Assert.False(e.Vm.ResultIsProblem);
        Assert.Single(e.Vm.ExactGroups);                                    // the group is gone; the other one is still listed
        Assert.DoesNotContain(e.Vm.ExactGroups, x => x.Model.Key == gKey);
        Assert.Equal($"1 duplicate found. You can free {Format.Size(sz2)}.", e.Vm.Headline);
        Assert.True(e.Vm.CanChangeThings);
        Assert.False(e.Vm.IsBusy);

        // Undo
        var undo = WpfHost.Run(() => new UndoViewModel(e.S));
        var action = Assert.Single(undo.Actions);
        Assert.Equal(2, action.Rows.Count);
        Assert.All(action.Rows, r => Assert.Contains("Duplicates to delete", r.WhereText));
        await WpfHost.Run(() => undo.PutBackAllAsync(action.Rows));
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(e.Ws.Pictures, "Trip"), "*.jpg").Count());
        Assert.Contains("Put back 2 photos. Scan again to see them in the groups.", undo.Message);
        Assert.Equal(2, undo.RestoredCount);
        WpfHost.Run(() => e.Vm.AfterUndo(undo.RestoredCount));
        Assert.StartsWith("Put back 2 photos.", e.Vm.ResultText);
        _ = paths;
    }

    [Fact]
    public async Task The_star_moves_with_one_click_is_remembered_and_decides_which_photo_stays()
    {
        using var e = new Step3Env("s3-star");
        e.Identical(3, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var first = g.Cards[0]; var second = g.Cards[1];
        Assert.True(first.IsKeeper); Assert.False(second.IsKeeper);
        Assert.Equal(Visibility.Visible, first.KeeperVisibility);
        Assert.Equal(Visibility.Collapsed, first.MakeKeeperVisibility);     // the keeper has no "make this the keeper"
        Assert.Equal(Visibility.Visible, second.MakeKeeperVisibility);
        Assert.Equal("★ Keeper", first.KeeperExplanation);

        WpfHost.Run(() => second.MakeKeeperCommand.Execute(null));

        Assert.True(g.Cards[0].File.Path == second.File.Path || g.Cards.Single(c => c.IsKeeper).File.Path == second.File.Path);
        var now = g.Cards.Single(c => c.IsKeeper);
        Assert.Equal(second.File.Path, now.File.Path);
        Assert.Equal(now.File.Path, g.Cards[0].File.Path);                  // keeper is shown first
        Assert.Equal("★ Keeper (your choice)", now.KeeperExplanation);
        Assert.Single(g.Cards, c => c.IsKeeper);                            // exactly one star
        var saved = new SettingsStore(e.Ws.Paths, e.Ws.Log).Load();
        Assert.Equal(second.File.Path, saved.KeeperPicks[g.Model.Key]);     // remembered on disk

        // the choice survives a rescan, and decides what is removed
        await e.ScanAsync();
        var again = e.Vm.ExactGroups.Single();
        Assert.Equal(second.File.Path, again.Model.Keeper.Path, ignoreCase: true);
        await e.RemoveAsync(again);
        Assert.True(File.Exists(second.File.Path));
        Assert.False(File.Exists(first.File.Path));
    }

    [Fact]
    public async Task A_star_cannot_be_moved_while_busy_or_off_a_shared_copy()
    {
        using var e = new Step3Env("s3-star-shared");
        e.Ws.Photo("x", Array.Empty<byte>());   // (folder placeholder; replaced below)
        File.Delete(Path.Combine(e.Ws.Pictures, "x"));
        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "Family"));
        e.Identical(4, "Family\\a.jpg", "Mine\\b.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var shared = g.Cards.Single(c => c.File.Path.Contains("Family"));
        var mine = g.Cards.Single(c => c.File.Path.Contains("Mine"));

        Assert.True(shared.IsKeeper);
        Assert.Contains("Shared folder", shared.Badge);
        Assert.Equal(Visibility.Collapsed, mine.MakeKeeperVisibility);      // no way to ask for it
        Assert.Equal(Visibility.Visible, mine.SharedMustStayVisibility);
        Assert.Equal("The copy in the shared folder always stays.", mine.SharedMustStayText);
        WpfHost.Run(() => e.Vm.SetKeeper(g, mine));                         // even if code asks for it anyway
        Assert.True(g.Cards.Single(c => c.IsKeeper).File.Path.Contains("Family"));
        Assert.Contains("always stays", e.Vm.ResultText);
        Assert.True(e.Vm.ResultIsProblem);
    }

    // -------------------------------------------------- Remove all exact copies: summary first

    [Fact]
    public async Task Remove_all_exact_copies_shows_the_summary_and_nothing_happens_if_Pete_says_no()
    {
        using var e = new Step3Env("s3-bulk");
        long total = 0;
        for (int i = 0; i < 3; i++) total += 2 * new FileInfo(e.Identical(10 + i, $"G{i}\\a.jpg", $"G{i}\\b.jpg", $"G{i}\\c.jpg")[0]).Length;
        await e.ScanAsync();
        Assert.Equal("Remove all exact copies (6 photos, " + Format.Size(total) + ")", e.Vm.RemoveAllExactText);
        e.Dialogs.Answer(DialogChoice.Cancel);

        await e.RemoveAllExactAsync();

        var ask = Assert.Single(e.Dialogs.Requests);
        Assert.Equal("Remove all exact copies?", ask.Title);
        Assert.Equal($"Remove 6 exact copies ({Format.Size(total)})? Every group keeps one photo.", ask.Message);
        Assert.Equal("Remove 6 photos", ask.PrimaryText);
        Assert.Equal(DialogChoice.Cancel, ask.Default);                      // Enter does not remove
        Assert.Contains("Recycle Bin", ask.Detail);
        Assert.Equal(9, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());   // nothing moved
        Assert.Equal("Nothing was removed.", e.Vm.ResultText);
        Assert.Empty(e.S.Journal.Entries());
        Assert.False(e.Vm.IsBusy);
    }

    [Fact]
    public async Task Remove_all_exact_copies_after_a_yes_removes_every_extra_and_keeps_one_per_group()
    {
        using var e = new Step3Env("s3-bulk-yes");
        for (int i = 0; i < 3; i++) e.Identical(20 + i, $"G{i}\\a.jpg", $"G{i}\\b.jpg", $"G{i}\\c.jpg");
        e.Identical(30, "S\\x.jpg", "S\\y.jpg");
        await e.ScanAsync();
        var keepers = e.Vm.ExactGroups.Select(g => g.Model.Keeper.Path).ToList();
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAllExactAsync();

        Assert.Single(e.Dialogs.Requests);
        Assert.StartsWith("Removed 7 photos", e.Vm.ResultText);
        Assert.All(keepers, k => Assert.True(File.Exists(k)));
        Assert.Equal(4, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count(f => !f.Contains("Duplicates to delete")));
        Assert.Empty(e.Vm.ExactGroups);
        Assert.Equal("No duplicates left.", e.Vm.Headline);
        Assert.Equal(Visibility.Collapsed, e.Vm.RemoveAllExactVisibility);
        Assert.Equal(7, e.S.Journal.Entries().Count(x => x.Status == EntryStatus.Done));
    }

    [Fact]
    public void The_only_bulk_commands_on_screen_are_for_exact_copies_and_same_photo_copies_never_look_alikes()
    {
        var bulk = typeof(MainViewModel).GetProperties().Where(p => p.Name.StartsWith("RemoveAll")).Select(p => p.Name).ToList();
        Assert.All(bulk, n => Assert.True(n.Contains("Exact") || n.Contains("SamePhoto"), n));
        Assert.DoesNotContain(typeof(MainViewModel).GetMethods(), m => m.Name.Contains("RemoveAllLook"));
        Assert.DoesNotContain(typeof(MainViewModel).GetProperties(), p => p.Name.Contains("RemoveAllLook"));
    }

    // -------------------------------------------------- shared folders: a second, clearly worded question

    private static async Task<Step3Env> SharedScene(string name)
    {
        var e = new Step3Env(name);
        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "Family Photos"));
        e.Identical(40, "Family Photos\\a.jpg", "Family Photos\\b.jpg", "Mine\\c.jpg");
        await e.ScanAsync();
        return e;
    }

    [Fact]
    public async Task Shared_photos_trigger_a_second_question_and_the_safe_answer_is_the_default()
    {
        using var e = await SharedScene("s3-shared-q");
        var g = e.Vm.ExactGroups.Single();
        Assert.True(g.Model.IsShared(g.Model.Keeper));
        e.Dialogs.Answer(DialogChoice.Cancel);

        await e.RemoveAsync(g);

        var ask = Assert.Single(e.Dialogs.Requests);
        Assert.Equal("Photos in a shared folder", ask.Title);
        Assert.Equal("1 of these photos is in a shared folder (\"Family Photos\"). Other people who use that folder will lose it too. Remove it as well?", ask.Message);
        Assert.Equal("Remove from shared folder too", ask.PrimaryText);
        Assert.Equal("Skip shared photos", ask.SecondaryText);
        Assert.Equal(DialogChoice.Secondary, ask.Default);                   // Enter = skip, never the removing button
        Assert.Equal(3, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());
        Assert.Equal("Nothing was removed.", e.Vm.ResultText);
    }

    [Fact]
    public async Task Skipping_the_shared_photos_removes_only_the_others()
    {
        using var e = await SharedScene("s3-shared-skip");
        var g = e.Vm.ExactGroups.Single();
        var sharedOther = g.Model.Others.Single(f => g.Model.IsShared(f));
        var mine = g.Model.Others.Single(f => !g.Model.IsShared(f));
        e.Dialogs.Answer(DialogChoice.Secondary);

        await e.RemoveAsync(g);

        Assert.Single(e.Dialogs.Requests);
        Assert.True(File.Exists(sharedOther.Path));
        Assert.True(File.Exists(g.Model.Keeper.Path));
        Assert.False(File.Exists(mine.Path));
        Assert.StartsWith("Removed 1 photo ", e.Vm.ResultText);
        Assert.Single(e.Vm.ExactGroups);                                     // the shared copy that stayed keeps the group alive
    }

    [Fact]
    public async Task Agreeing_to_the_second_question_removes_the_shared_photo_too()
    {
        using var e = await SharedScene("s3-shared-yes");
        var g = e.Vm.ExactGroups.Single();
        e.Dialogs.Answer(DialogChoice.Primary);

        await e.RemoveAsync(g);

        Assert.StartsWith("Removed 2 photos", e.Vm.ResultText);
        Assert.Single(Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Where(f => !f.Contains("Duplicates to delete")));
        Assert.True(File.Exists(g.Model.Keeper.Path));
    }

    [Fact]
    public async Task When_everything_to_remove_is_shared_and_Pete_skips_it_says_so_and_nothing_happens()
    {
        using var e = new Step3Env("s3-shared-all");
        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "Family Photos"));
        e.Identical(41, "Family Photos\\a.jpg", "Family Photos\\b.jpg");
        await e.ScanAsync();
        e.Dialogs.Answer(DialogChoice.Secondary);

        await e.RemoveAsync(e.Vm.ExactGroups.Single());

        Assert.Contains("all the photos to remove are in shared folders", e.Vm.ResultText);
        Assert.Equal(2, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());
    }

    // -------------------------------------------------- cloud folders: one line, asked once

    [Fact]
    public async Task Removing_from_a_cloud_folder_asks_one_plain_question_naming_the_service()
    {
        using var e = new Step3Env("s3-cloud", cloudRelative: new[] { "OneDrive" });
        e.Identical(50, "OneDrive\\a.jpg", "OneDrive\\b.jpg", "OneDrive\\c.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        Assert.All(g.Cards, c => Assert.Contains("OneDrive", c.Badge));
        e.Dialogs.Answer(DialogChoice.Cancel);

        await e.RemoveAsync(g);

        var ask = Assert.Single(e.Dialogs.Requests);
        Assert.Equal("These photos are in OneDrive. Removing them here also removes them from OneDrive online and from your other devices. Remove them?", ask.Message);
        Assert.Equal(DialogChoice.Cancel, ask.Default);
        Assert.Equal(3, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());

        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAsync(g);
        Assert.Equal(2, e.Dialogs.Requests.Count);                           // asked once for this action (two photos)
        Assert.StartsWith("Removed 2 photos", e.Vm.ResultText);
    }

    [Fact]
    public async Task All_three_questions_come_in_the_right_order_when_all_three_apply()
    {
        using var e = new Step3Env("s3-order", cloudRelative: new[] { "OneDrive" });
        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "OneDrive", "Shared"));
        e.Identical(60, "OneDrive\\Shared\\a.jpg", "OneDrive\\Shared\\b.jpg", "OneDrive\\mine.jpg");
        await e.ScanAsync();
        e.Dialogs.Answer(DialogChoice.Primary, DialogChoice.Primary, DialogChoice.Primary);

        await e.RemoveAllExactAsync();

        Assert.Equal(new[] { "Remove all exact copies?", "Photos in a shared folder", "Photos in the cloud" }, e.Dialogs.Requests.Select(r => r.Title).ToArray());
        Assert.StartsWith("Removed 2 photos", e.Vm.ResultText);
    }

    // -------------------------------------------------- refusals, left-out photos, failures are all said out loud

    [Fact]
    public async Task When_the_safety_check_refuses_the_screen_says_nothing_was_removed_and_why()
    {
        using var e = new Step3Env("s3-refuse");
        e.Identical(70, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        File.Delete(g.Model.Keeper.Path);   // the photo to keep disappears behind the app's back

        await e.RemoveAsync(g);

        Assert.StartsWith("PhotoTwins did not remove anything.", e.Vm.ResultText);
        Assert.Contains("photo to keep", e.Vm.ResultText);
        Assert.True(e.Vm.ResultIsProblem);
        Assert.True(File.Exists(g.Model.Others.Single().Path));
        Assert.Single(e.Vm.ExactGroups);
    }

    [Fact]
    public async Task A_photo_that_changed_after_the_scan_is_reported_with_a_details_list()
    {
        using var e = new Step3Env("s3-dropped");
        e.Identical(71, "a.jpg", "b.jpg", "c.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var edited = g.Model.Others.First();
        File.AppendAllText(edited.Path, "edited later");

        await e.RemoveAsync(g);

        Assert.Contains("Removed 1 photo", e.Vm.ResultText);
        Assert.Contains("1 photo was left out because it changed or disappeared after the scan", e.Vm.ResultText);
        Assert.True(e.Vm.ResultIsProblem);
        Assert.Equal(Visibility.Visible, e.Vm.ResultDetailsVisibility);
        var win = WpfHost.Run(() => new ProblemsWindow(e.Vm.LastRemoval!, e.S.Log));
        Assert.Equal(1, WpfHost.Run(() => win.LeftOutItems.Items.Count));
        Assert.Contains("changed after the scan", WpfHost.Run(() => ((PhotoTwins.Core.Scanning.LeftOut)win.LeftOutItems.Items[0]!).Reason));
        Assert.True(File.Exists(edited.Path));
        Assert.Single(e.Vm.ExactGroups);   // the edited photo is still there with the keeper
    }

    [Fact]
    public async Task A_removal_that_cannot_move_a_photo_lists_it_and_carries_on()
    {
        using var e = new Step3Env("s3-failed");
        e.Identical(72, "a.jpg", "b.jpg", "c.jpg");
        File.WriteAllText(Path.Combine(e.Ws.Pictures, "Duplicates to delete"), "a FILE where the folder must go");   // no photo can be moved
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();

        await e.RemoveAsync(g);

        Assert.StartsWith("Nothing was removed.", e.Vm.ResultText);
        Assert.Contains("Couldn't remove 2 photos.", e.Vm.ResultText);
        Assert.True(e.Vm.ResultIsProblem);
        Assert.Equal(3, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg").Count());
        var win = WpfHost.Run(() => new ProblemsWindow(e.Vm.LastRemoval!, e.S.Log));
        Assert.Equal(2, WpfHost.Run(() => win.ProblemsItems.Items.Count));
    }

    // -------------------------------------------------- busy states

    [Fact]
    public async Task While_removing_every_other_button_is_off_and_Stop_ends_it_cleanly_leaving_the_rest_untouched()
    {
        using var e = new Step3Env("s3-busy", new NoBinRules { DelayMs = 250 });
        for (int i = 0; i < 6; i++) e.Identical(80 + i, $"G{i}\\a.jpg", $"G{i}\\b.jpg");
        await e.ScanAsync();
        e.Dialogs.Answer(DialogChoice.Primary);

        var removing = WpfHost.Run(() => e.Vm.RemoveAllExactAsync());
        Assert.True(SpinWait.SpinUntil(() => e.Vm.StatusText.StartsWith("Removing") && e.Vm.ProgressValue >= 1, 30000));

        Assert.True(e.Vm.IsBusy);
        Assert.False(e.Vm.CanChangeFolders);
        Assert.False(e.Vm.CanUndo);
        Assert.False(e.Vm.ScanCommand.CanExecute(null));
        Assert.False(e.Vm.RemoveAllExactCommand.CanExecute(null));
        Assert.False(e.Vm.ExactGroups[0].RemoveCommand.CanExecute(null));
        Assert.False(e.Vm.ExactGroups[0].Cards[1].MakeKeeperCommand.CanExecute(null));
        Assert.False(e.Vm.PauseResumeCommand.CanExecute(null));
        Assert.True(e.Vm.StopCommand.CanExecute(null));
        Assert.Equal(System.Windows.Visibility.Visible, e.Vm.ProgressVisibility);

        WpfHost.Run(() => e.Vm.StopCommand.Execute(null));
        await removing;

        Assert.False(e.Vm.IsBusy);
        Assert.Contains("You stopped it part way", e.Vm.ResultText);
        int removed = e.S.Journal.Entries().Count(x => x.Status == EntryStatus.Done);
        Assert.InRange(removed, 1, 5);
        Assert.Equal(12 - removed, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count(f => !f.Contains("Duplicates to delete")));
        Assert.True(e.Vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Closing_the_window_in_the_middle_of_a_removal_stops_it_first_and_everything_done_can_be_undone()
    {
        using var e = new Step3Env("s3-closewin", new NoBinRules { DelayMs = 250 });
        for (int i = 0; i < 6; i++) e.Identical(90 + i, $"G{i}\\a.jpg", $"G{i}\\b.jpg");
        await e.ScanAsync();
        e.Dialogs.Answer(DialogChoice.Primary);
        var closed = new TaskCompletionSource();
        var win = WpfHost.Run(() => { var w = new MainWindow(e.Vm); w.Closed += (_, _) => closed.TrySetResult(); return w; });
        var removing = WpfHost.Run(() => e.Vm.RemoveAllExactAsync());
        Assert.True(SpinWait.SpinUntil(() => e.Vm.IsBusy && e.Vm.StatusText.StartsWith("Removing") && e.Vm.ProgressValue >= 1, 30000));

        WpfHost.Run(() => win.Close());

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await removing;
        Assert.False(e.Vm.IsBusy);
        var done = e.S.Journal.Entries().Where(x => x.Status == EntryStatus.Done).ToList();
        Assert.InRange(done.Count, 1, 5);
        Assert.Empty(e.S.Journal.Entries().Where(x => x.Status == EntryStatus.Starting));   // nothing left half-done
        Assert.All(done, d => Assert.True(File.Exists(d.Destination!)));
    }

    [Fact]
    public async Task Nothing_removes_while_a_scan_is_running_and_the_buttons_say_so()
    {
        using var e = new Step3Env("s3-scanning");
        e.Identical(100, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var gate = new ManualResetEventSlim();
        e.Ws.Fs.BeforeOpen = _ => gate.Wait(TimeSpan.FromSeconds(30));
        var scanning = WpfHost.Run(() => e.Vm.StartScanAsync());
        Assert.True(SpinWait.SpinUntil(() => e.Vm.IsScanning, 10000));

        Assert.False(e.Vm.CanChangeThings);
        Assert.False(g.RemoveCommand.CanExecute(null));
        await e.RemoveAsync(g);   // even if asked directly: nothing happens
        Assert.Empty(e.Dialogs.Requests);
        Assert.Equal(2, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg").Count());

        gate.Set();
        await scanning;
    }

    // -------------------------------------------------- the missing-date line, the Undo list, start-up recovery

    [Fact]
    public async Task A_same_photo_group_where_a_photo_has_no_date_shows_one_plain_warning_line()
    {
        using var e = new Step3Env("s3-nodate");
        using var scene = TestScenes.Scene(1);
        e.Ws.Photo("with date.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, Mine));
        e.Ws.Photo("Sent in chat\\IMG-20261012-WA0003.jpg", TestScenes.ChatAppVersion(scene));
        await e.ScanAsync();
        var g = Assert.Single(e.Vm.SameGroups);

        Assert.Equal(Visibility.Visible, g.NoDateWarningVisibility);
        Assert.Equal("Some of these have no date info. Check they're really the same photo before removing.", g.NoDateWarning);
        Assert.Equal("with date.jpg", g.Cards[0].Name);   // the one with date and place info keeps the star
        ScreenShots.WaitForPictures(e.Vm.SameGroups);
        ScreenShots.Take(e.Vm, "step3", "no-date-warning");
    }

    [Fact]
    public async Task Exact_groups_never_show_the_missing_date_line()
    {
        using var e = new Step3Env("s3-nodate-exact");
        e.Identical(101, "a.jpg", "b.jpg");
        await e.ScanAsync();
        Assert.Equal(Visibility.Collapsed, e.Vm.ExactGroups.Single().NoDateWarningVisibility);
    }

    [Fact]
    public async Task A_huge_removal_lists_only_its_first_photos_in_Undo_but_Put_back_all_restores_every_one()
    {
        using var e = new Step3Env("s3-undo-big");
        for (int i = 0; i < 230; i++) e.Identical(1000 + i, $"G{i % 10}\\n{i}a.jpg", $"G{i % 10}\\n{i}b.jpg");
        await e.ScanAsync();
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllExactAsync();
        Assert.StartsWith("Removed 230 photos", e.Vm.ResultText);

        var undo = WpfHost.Run(() => new UndoViewModel(e.S));
        var action = Assert.Single(undo.Actions);

        Assert.Equal(230, action.AllRows.Count);
        Assert.Equal(200, action.Rows.Count);
        Assert.Contains("…and 30 more", action.MoreText);
        await WpfHost.Run(() => undo.PutBackAllAsync(action.AllRows));
        Assert.Equal(460, Directory.EnumerateFiles(e.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count(f => !f.Contains("Duplicates to delete")));
        Assert.Equal(230, undo.RestoredCount);
    }

    [Fact]
    public async Task The_Undo_list_shows_newest_first_says_where_each_photo_went_and_reports_the_ones_that_cannot_come_back()
    {
        using var e = new Step3Env("s3-undo");
        e.Identical(110, "A\\a.jpg", "A\\b.jpg");
        e.Identical(111, "B\\x.jpg", "B\\y.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(ExactWith(e, "a.jpg"));
        await Task.Delay(1100);
        await e.RemoveAsync(ExactWith(e, "x.jpg"));

        var undo = WpfHost.Run(() => new UndoViewModel(e.S));

        Assert.Equal(2, undo.Actions.Count);
        Assert.Contains("1 photo removed", undo.Actions[0].Header);
        var newest = undo.Actions[0].Rows.Single();
        Assert.Contains(@"\B\", newest.Entry.OriginalPath);   // the second removal was in folder B
        Assert.Equal("In the \"Duplicates to delete\" folder", newest.WhereText);

        // someone clears out the holding folder: that photo reports as gone, the other still comes back
        var older = undo.Actions[1].Rows.Single();
        File.Delete(older.Entry.Destination!);
        await WpfHost.Run(() => undo.PutBackAsync(older.Entry));
        Assert.Contains("no longer in the \"Duplicates to delete\" folder", undo.Message);
        Assert.Contains("No longer there", older.StatusText);
        Assert.Equal(Visibility.Collapsed, older.ButtonVisibility);
        await WpfHost.Run(() => undo.PutBackAsync(newest.Entry));
        Assert.True(File.Exists(newest.Entry.OriginalPath));
        Assert.Equal("Put back", newest.StatusText);
    }

    [Fact]
    public void An_interrupted_removal_from_last_time_is_put_right_and_announced_at_start_up()
    {
        using var ws = new TestWorkspace("s3-recover");
        var a = TestPhotoFactory.Write(Path.Combine(ws.Pictures, "a.jpg"), TestPhotoFactory.StandIn(120, 5000));
        var journal = new UndoJournal(Path.Combine(ws.Paths.DataDir, "undo-journal.jsonl"), ws.Log);
        journal.Append(new JournalEntry("e1", "act", DateTime.Now, a, 5000, 1, "00", "k", "keeper", RemoveMethod.Folder, EntryStatus.Starting,
            Path.Combine(ws.Pictures, "Duplicates to delete", "a.jpg"), null));   // started, but the photo never moved

        using var s = new AppServices(ws.Paths, ws.Fs, ws.Policy, ws.Pictures, null, new AppServicesOptions { Volumes = new NoBinRules() });
        var vm = WpfHost.Run(() => new MainViewModel(s, new FakeDialogs()));

        Assert.Contains("stopped in the middle of removing photos", vm.Banner);
        Assert.Contains("1 hadn't been touched", vm.Banner);
        Assert.Equal(EntryStatus.Failed, s.Journal.Entries().Single().Status);
        Assert.True(File.Exists(a));
    }

    // -------------------------------------------------- pictures at 100% and 125%, light and dark

    [Fact]
    public async Task The_new_screens_fit_every_size_and_read_clearly()
    {
        using var e = new Step3Env("s3-pictures", cloudRelative: new[] { "OneDrive" });
        e.S.Settings.SharedFolders.Add(Path.Combine(e.Ws.Pictures, "Family"));
        Step2Tests.MakeBirthdayPhotos(e.Ws);
        e.Identical(130, "Family\\IMG_5001.jpg", "Family\\IMG_5001 copy.jpg", "Mine\\IMG_5001.jpg");
        e.Identical(131, "OneDrive\\IMG_6001.jpg", "OneDrive\\IMG_6001 (1).jpg");
        await e.ScanAsync();
        ScreenShots.WaitForPictures(e.Vm.ExactGroups, 6);
        ScreenShots.Take(e.Vm, "step3", "exact-tab", height: 1100);
        e.Vm.SelectedTab = 1;
        ScreenShots.WaitForPictures(e.Vm.SameGroups);
        ScreenShots.Take(e.Vm, "step3", "same-tab", height: 1100);

        // after removing one group: the result line and the updated headline
        e.Vm.SelectedTab = 0;
        var oneDrive = ExactWith(e, "IMG_6001.jpg");
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAsync(oneDrive);
        ScreenShots.Take(e.Vm, "step3", "after-removal", height: 900);

        // the question windows
        ScreenShots.TakeConfirm(new ConfirmRequest("Remove all exact copies?", "Remove 1,284 exact copies (6.2 GB)? Every group keeps one photo.", "Remove 1,284 photos", null, "Cancel", DialogChoice.Cancel,
            "Removed photos go to the Recycle Bin, or to a \"Duplicates to delete\" folder on drives that don't have one. You can undo it from the Undo list."), "bulk-question");
        ScreenShots.TakeConfirm(new ConfirmRequest("Photos in a shared folder", "3 of these photos are in a shared folder (\"Family Photos\"). Other people who use that folder will lose them too. Remove them as well?",
            "Remove from shared folder too", "Skip shared photos", "Cancel", DialogChoice.Secondary), "shared-question");
        ScreenShots.TakeConfirm(new ConfirmRequest("Photos in the cloud", "These photos are in OneDrive. Removing them here also removes them from OneDrive online and from your other devices. Remove them?",
            "Remove", null, "Cancel", DialogChoice.Cancel), "cloud-question");

        // the Undo list
        var undo = WpfHost.Run(() => new UndoViewModel(e.S));
        ScreenShots.TakeUndo(undo, "undo-list");
    }
}
