using System.Security.Cryptography;
using System.Windows;
using ImageMagick;
using PhotoTwins.Core.Removal;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>
/// Undo into the groups checks the contents and never freezes the window; a picture that failed is tried again on the next scan;
/// an honest message if the main screen can't be updated after an Undo; and the calm pictures for the user.
/// </summary>
[Collection("WPF")]
public class UndoAndPictureFixTests
{
    private static UndoViewModel NewUndo(Step3Env e, IRestoreTarget? target = null) => WpfHost.Run(() => new UndoViewModel(e.S, target ?? e.Vm));
    private static string[] Paths(GroupVm g) => g.Model.Files.Select(f => f.Path).ToArray();

    private static PhotoNow NowOf(Step3Env e, string path)
    {
        using var o = e.Ws.Fs.OpenRead(path);
        return new PhotoNow(o.Info.Size, o.Info.LastWriteUtcTicks, o.Info.Identity, SHA256.HashData(o.Stream));
    }

    private sealed class ThrowingTarget : IRestoreTarget
    {
        public RestoreOutcome RestorePhotos(IReadOnlyList<RestoredPhoto> restored) => throw new InvalidOperationException("the screen broke");
    }

    // ------------------------------------------------------------------ A1: Undo into groups checks the contents

    [Fact]
    public async Task A_photo_edited_in_the_holding_folder_is_put_back_on_disk_but_not_into_its_group()
    {
        using var e = new Step3Env("r2-edited");                          // (this drive, as pretended, has no Recycle Bin: the holding-folder route)
        e.Identical(1, "G\\a.jpg", "G\\b.jpg", "G\\c.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var keeper = g.Model.Keeper.Path;
        await e.RemoveAsync(g);
        var undo = NewUndo(e);
        var rows = undo.Actions.Single().AllRows;
        var edited = rows[0].Entry; var fine = rows[1].Entry;
        var held = edited.Destination!;
        var bytes = File.ReadAllBytes(held); bytes[100] ^= 0xFF;          // one byte differs: same size, different contents
        File.WriteAllBytes(held, bytes);

        await WpfHost.Run(() => undo.PutBackAllAsync(rows));

        Assert.True(File.Exists(edited.OriginalPath)); Assert.True(File.Exists(fine.OriginalPath));   // both are back on disk ...
        var back = e.Vm.ExactGroups.Single();
        Assert.Equal(2, back.Model.Files.Count);                           // ... but only the unchanged one is back in the group
        Assert.Contains(back.Model.Files, f => f.Path == fine.OriginalPath);
        Assert.DoesNotContain(back.Model.Files, f => f.Path == edited.OriginalPath);
        Assert.Equal(keeper, back.Model.Keeper.Path);
        Assert.Contains("1 of them are back in their groups", undo.Message);
        Assert.Contains("scan again", undo.Message);
        Assert.Contains("scan again", e.Vm.ResultText);
        Assert.Contains("contents don't match", undo.Message);             // and the Undo window says so about that photo
        Assert.StartsWith("Remove all exact copies (1 photo", e.Vm.RemoveAllExactText);   // the count only includes the photo that really is the same

        await e.ScanAsync();                                              // the next scan sees what is really on disk: the edited photo is no longer an exact copy
        Assert.DoesNotContain(e.Vm.ExactGroups.SelectMany(x => x.Model.Files), f => f.Path == edited.OriginalPath);
        Assert.Equal(3, e.Vm.SameGroups.Single().Model.Files.Count);      // (it is a slightly different version of the same picture now)
    }

    [Fact]
    public async Task The_main_screen_accepts_a_restored_photo_only_if_its_size_and_SHA_256_match_the_scan()
    {
        using var e = new Step3Env("r2-sha");
        e.Identical(2, "a.jpg", "b.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        var entry = e.S.Journal.Entries().Single(x => x.Status == EntryStatus.Done);
        var put = e.S.Mutations.PutBack(entry);
        Assert.True(put.Ok);
        var now = NowOf(e, put.Entry.OriginalPath);

        RestoreOutcome Try(PhotoNow? n) => WpfHost.Run(() => e.Vm.RestorePhotos(new[] { new RestoredPhoto(put.Entry, n) }));

        Assert.Equal(new RestoreOutcome(0, 1), Try(now with { Sha256 = new byte[32] }));        // another SHA-256: left out
        Assert.Equal(new RestoreOutcome(0, 1), Try(now with { Size = now.Size + 1 }));           // another size: left out
        Assert.Equal(new RestoreOutcome(0, 1), Try(null));                                       // could not be checked (online-only, unreadable): left out
        Assert.Empty(e.Vm.ExactGroups);
        Assert.Contains("scan again", e.Vm.ResultText);

        Assert.Equal(new RestoreOutcome(1, 0), Try(now));                                        // the very photo the scan saw: back in its group
        var g = e.Vm.ExactGroups.Single();
        Assert.Equal(2, g.Model.Files.Count);
        Assert.Equal(now.Identity, g.Model.Files.Single(f => f.Path == put.Entry.OriginalPath).Identity);   // with its up-to-date Windows file ID
    }

    [Fact]
    public async Task Putting_back_300_photos_in_one_action_never_reads_a_file_on_the_screen_thread_and_keeps_it_responsive()
    {
        using var e = new Step3Env("r2-300b");
        for (int i = 0; i < 100; i++) e.Identical(3000 + i, $"G{i:000}\\a.jpg", $"G{i:000}\\b.jpg", $"G{i:000}\\c.jpg", $"G{i:000}\\d.jpg");   // 300 extras
        await e.ScanAsync();
        e.Dialogs.Answer(DialogChoice.Primary);
        await e.RemoveAllExactAsync();
        var undo = NewUndo(e);
        var rows = undo.Actions.Single().AllRows;
        Assert.Equal(300, rows.Count);
        int ui = WpfHost.Run(() => Environment.CurrentManagedThreadId);
        int onUi = 0;
        e.Ws.Fs.BeforeOpen = _ => { if (Environment.CurrentManagedThreadId == ui) Interlocked.Increment(ref onUi); };
        // a heartbeat on the screen thread while the Undo runs: if it ever stalls, the window would have frozen
        long worstGapMs = 0, last = Environment.TickCount64; bool running = true;
        WpfHost.Run(() =>
        {
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { var now = Environment.TickCount64; worstGapMs = Math.Max(worstGapMs, now - last); last = now; if (!running) timer.Stop(); };
            timer.Start();
        });

        await WpfHost.Run(() => undo.PutBackAllAsync(rows));
        running = false;

        Assert.Equal(0, onUi);
        Assert.Equal(100, e.Vm.ExactGroups.Count);
        Assert.All(e.Vm.ExactGroups, g => Assert.Equal(4, g.Model.Files.Count));
        Assert.InRange(worstGapMs, 0, 1500);                               // the screen thread never stood still for long (the main screen's own list update is the only work on it)
    }

    [Fact]
    public async Task Undo_never_reads_an_online_only_photo_and_leaves_it_out_of_the_group()
    {
        using var e = new Step3Env("r2-online");
        e.Identical(3, "a.jpg", "b.jpg", "c.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        var undo = NewUndo(e);
        var rows = undo.Actions.Single().AllRows;
        var sneaky = rows[0].Entry.OriginalPath;
        // the photo becomes online-only the moment after Undo has checked it came back (cloud sync dehydrates it)
        e.Ws.Fs.BeforeOpen = p => { if (string.Equals(p, sneaky, StringComparison.OrdinalIgnoreCase)) e.Ws.Fs.BecameOnlineOnly.Add(sneaky); };

        await WpfHost.Run(() => undo.PutBackAllAsync(rows));

        Assert.Empty(e.Ws.Fs.ForbiddenOpens);                              // nothing online-only was ever opened
        Assert.True(File.Exists(sneaky));
        var g = e.Vm.ExactGroups.Single();
        Assert.Equal(2, g.Model.Files.Count);
        Assert.DoesNotContain(g.Model.Files, f => f.Path == sneaky);
        Assert.Contains("scan again", e.Vm.ResultText);
    }

    [Fact]
    public async Task A_photo_that_is_online_only_when_it_comes_back_is_not_read_to_check_it()
    {
        using var e = new Step3Env("r2-online2");
        e.Identical(4, "a.jpg", "b.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        var undo = NewUndo(e);
        var row = undo.Actions.Single().AllRows.Single();
        e.Ws.Fs.BecameOnlineOnly.Add(row.Entry.OriginalPath);

        await WpfHost.Run(() => undo.PutBackAllAsync(new[] { row }));

        Assert.Empty(e.Ws.Fs.ForbiddenOpens);
        Assert.True(File.Exists(row.Entry.OriginalPath));
        Assert.Contains("online-only", undo.Message);
        Assert.Empty(e.Vm.ExactGroups);
    }

    [Fact]
    public async Task The_Recycle_Bin_route_checks_the_contents_too_and_comes_back_into_the_group()
    {
        using var e = new Step3Env("r2-bin", realBin: true);
        e.Identical(5, "a.jpg", "b.jpg", "c.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var files = Paths(g);
        await e.RemoveAsync(g);
        Assert.Equal(RemoveMethod.Recycle, e.S.Journal.Entries().First(x => x.Status == EntryStatus.Done).Method);
        var undo = NewUndo(e);
        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));
        Assert.Equal(files, Paths(e.Vm.ExactGroups.Single()));
        Assert.Contains("They are back in their groups on the main screen", undo.Message);
        await e.RemoveAsync(e.Vm.ExactGroups.Single());                   // and the restored photos pass the safety check again
        Assert.StartsWith("Removed 2 photos", e.Vm.ResultText);
    }

    // ------------------------------------------------------------------ A2: a picture that failed is retried on the next scan

    [Fact]
    public async Task A_picture_that_could_not_be_drawn_is_drawn_after_the_next_scan_once_the_photo_can_be_read()
    {
        using var e = new Step3Env("r2-retry");
        for (int i = 0; i < 12; i++)
        {
            using var scene = TestScenes.Scene(300 + i, w: 96, h: 72);
            var bytes = TestScenes.Encode(scene, MagickFormat.Jpeg, 90);
            e.Ws.Photo($"G{i:00}\\a.jpg", bytes); e.Ws.Photo($"G{i:00}\\b.jpg", bytes);
        }
        await e.ScanAsync();
        Assert.Equal(12, e.Vm.ExactGroups.Count);
        var card = e.Vm.ExactGroups[11].Cards[0];                          // far enough down that nothing asked for its picture yet
        var key = card.ThumbnailKey; var path = card.FullPath;
        Assert.Null(e.S.Thumbnails.TryGet(key));
        e.S.ThumbStore.Remove(new[] { key });                              // so it must be made from the photo itself
        e.Ws.Fs.BecameOnlineOnly.Add(path);                                // ... which can't be read right now (OneDrive busy with it)

        Assert.Null(WpfHost.Run(() => card.Picture));
        Assert.True(SpinWait.SpinUntil(() => e.S.Thumbnails.FailureOf(key) != null, 30000));
        Assert.StartsWith("Couldn't draw this picture.", WpfHost.Run(() => card.PictureStatus));

        e.Ws.Fs.BecameOnlineOnly.Remove(path);                             // the photo can be read again
        Assert.Null(WpfHost.Run(() => card.Picture));                      // (still remembered as failed until new results are shown)
        await e.ScanAsync();

        Assert.Null(e.S.Thumbnails.FailureOf(key));                        // the new results forgot the failure
        var again = e.Vm.ExactGroups.Single(x => x.Cards[0].ThumbnailKey == key);
        ScreenShots.WaitForPictures(new[] { again });
        Assert.NotNull(WpfHost.Run(() => again.Cards[0].Picture));
        Assert.Equal("", WpfHost.Run(() => again.Cards[0].PictureStatus));
    }

    [Fact]
    public async Task The_picture_service_forgets_its_failures_only_when_asked_and_then_draws_a_photo_that_has_become_readable()
    {
        using var ws = new TestWorkspace("r2-retry-unit");
        var svc = new ThumbnailService(ws.Fs, ws.Log, null);
        var path = Path.Combine(ws.Pictures, "late.jpg");
        const string key = "1-AA";

        svc.Request(key, path, () => { });                                 // the file is not there yet
        Assert.True(SpinWait.SpinUntil(() => svc.FailureOf(key) != null, 30000));
        using (var scene = TestScenes.Scene(9, w: 96, h: 72)) TestPhotoFactory.Write(path, TestScenes.Encode(scene, MagickFormat.Jpeg, 90));
        svc.Request(key, path, () => { });
        await Task.Delay(200);
        Assert.NotNull(svc.FailureOf(key));                                // still remembered: asking again changes nothing by itself
        Assert.Null(svc.TryGet(key));

        svc.ClearFailures();
        Assert.Null(svc.FailureOf(key));
        svc.Request(key, path, () => { });
        Assert.True(SpinWait.SpinUntil(() => svc.TryGet(key) != null, 30000));
        Assert.Null(svc.FailureOf(key));
    }

    [Fact]
    public async Task Scanning_again_forgets_failures_even_for_pictures_nobody_asked_for_again()
    {
        using var e = new Step3Env("r2-forget");
        e.Identical(6, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var missing = Path.Combine(e.Ws.Pictures, "never-there.jpg");
        e.S.Thumbnails.Request("5-BB", missing, () => { });
        Assert.True(SpinWait.SpinUntil(() => e.S.Thumbnails.FailureOf("5-BB") != null, 30000));

        await e.ScanAsync();

        Assert.Null(e.S.Thumbnails.FailureOf("5-BB"));
    }

    // ------------------------------------------------------------------ A3: an honest message if the main screen can't be updated

    [Fact]
    public async Task If_the_main_screen_fails_to_update_after_Undo_the_message_says_the_photos_are_back_and_the_error_is_logged()
    {
        using var e = new Step3Env("r2-throw");
        e.Identical(7, "a.jpg", "b.jpg", "c.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        var undo = NewUndo(e, new ThrowingTarget());
        var rows = undo.Actions.Single().AllRows;

        await WpfHost.Run(() => undo.PutBackAllAsync(rows));               // does not throw

        Assert.All(rows, r => Assert.True(File.Exists(r.Entry.OriginalPath)));                 // the photos ARE back on disk
        Assert.All(rows, r => Assert.Equal("Put back", r.StatusText));
        Assert.Contains("Put back 2 photos.", undo.Message);
        Assert.Contains("back in their folders", undo.Message);
        Assert.Contains("main screen couldn't be updated", undo.Message);
        Assert.Contains("Scan again to see them in the groups", undo.Message);
        Assert.DoesNotContain("They are back in their groups", undo.Message);                  // it does not claim the screen was updated
        Assert.False(undo.IsBusy);
        Assert.Equal(2, undo.RestoredCount);
        var log = string.Join("\n", Directory.EnumerateFiles(e.Ws.Paths.LogDir, "*.log").Select(File.ReadAllText));
        Assert.Contains("the screen broke", log);
        Assert.Contains("main screen couldn't be updated", log);
        Assert.Empty(e.Vm.ExactGroups);                                    // (the real main screen was never told, so it still shows the old list)
    }

    [Fact]
    public async Task With_a_single_photo_the_failed_update_message_reads_in_the_singular()
    {
        using var e = new Step3Env("r2-throw1");
        e.Identical(8, "a.jpg", "b.jpg");
        await e.ScanAsync();
        await e.RemoveAsync(e.Vm.ExactGroups.Single());
        var undo = NewUndo(e, new ThrowingTarget());

        await WpfHost.Run(() => undo.PutBackAllAsync(undo.Actions.Single().AllRows));
        // (one photo: "Put back all" has nothing special to do; the single-row route says the same)
        Assert.Contains("Put back 1 photo.", undo.Message);
        Assert.Contains("It is back in its folder", undo.Message);
        Assert.Contains("Scan again to see it in the groups", undo.Message);
    }

    // ------------------------------------------------------------------ B: the pictures for the user (the glossy look), with every kind of group

    [Fact]
    public async Task Pictures_for_Pete_main_window_the_Same_tab_with_the_remove_all_bar_and_the_question_at_every_size()
    {
        using var e = new Step3Env("r2-calm");
        for (int i = 0; i < 3; i++)
        {
            using var scene = TestScenes.Scene(500 + i * 11);
            var bytes = TestScenes.Encode(scene, MagickFormat.Jpeg, 90);
            e.Ws.Photo($"Trip {i}\\IMG_{i:000}.jpg", bytes); e.Ws.Photo($"Trip {i}\\IMG_{i:000} (1).jpg", bytes);
        }
        SameData.Group(e.Ws, 1, "Holiday", identicalCopy: true);
        SameData.Group(e.Ws, 2, "Garden");
        SameData.Group(e.Ws, 3, "Party", third: true);
        SameData.Group(e.Ws, 4, "Chat", dated: false);
        await e.ScanAsync();

        ScreenShots.WaitForPictures(e.Vm.ExactGroups, 4);
        e.Vm.SelectedTab = 0;
        ScreenShots.Take(e.Vm, "gloss", "full-main-window", width: 1280, height: 1100);
        e.Vm.SelectedTab = 1;
        ScreenShots.WaitForPictures(e.Vm.SameGroups, 4);
        ScreenShots.Take(e.Vm, "gloss", "full-same-tab", width: 1280, height: 1100);

        e.Dialogs.Answer(DialogChoice.Cancel);
        await e.RemoveAllSamePhotoAsync();
        ScreenShots.TakeConfirm(e.Dialogs.Requests.Single(), "remove-all-same-question", step: "gloss");

        foreach (var name in new[] { "full-main-window", "full-same-tab", "remove-all-same-question" })
            foreach (var look in new[] { "light", "dark" })
                Assert.True(File.Exists(Path.Combine(ScreenShots.Folder("gloss"), $"{name}-{look}-125.png")), name + look);
    }
}
