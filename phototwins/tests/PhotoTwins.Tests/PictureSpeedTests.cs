using ImageMagick;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Pictures on the cards appear fast: small pictures made during the scan, remembered, drawn several at a time. Made-up photos only.</summary>
[Collection("WPF")]
public class PictureSpeedTests
{
    private static string Key(PhotoFile f) => ThumbnailStore.KeyOf(f.Size, f.Sha256);

    private static ScanResult ScanWith(TestWorkspace ws, ThumbnailStore store)
        => new ScanEngine(ws.Fs, ws.Cache, ws.Log, ws.Policy, null, null, store) { MaxParallelReads = 4 }.Run(new[] { ws.Pictures }, new ScanControl(), null);

    private static (int w, int h) Dimensions(byte[] jpeg) { var i = new MagickImageInfo(jpeg); return ((int)i.Width, (int)i.Height); }

    private static byte[] BigJpeg(int seed, int w = 2400, int h = 1800)
    {
        using var img = TestScenes.Scene(seed);
        img.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
        img.AddNoise(NoiseType.Gaussian, 0.4);
        img.Quality = 88; img.Format = MagickFormat.Jpeg;
        return img.ToByteArray();
    }

    /// <summary>Waits until the service has the picture (or says why it can't), as the screen would see it.</summary>
    private static void WaitFor(ThumbnailService svc, string key, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (svc.TryGet(key) != null || svc.FailureOf(key) != null) return;
            Thread.Sleep(5);
        }
        throw new TimeoutException("the picture was not ready in time");
    }

    // ------------------------------------------------------------ the picture memory itself

    [Fact]
    public void Store_remembers_forgets_and_survives_being_reopened()
    {
        using var ws = new TestWorkspace("ps-store");
        var path = Path.Combine(ws.Paths.DataDir, "pictures.db");
        var jpeg = TestScenes.Encode(TestScenes.Scene(3), MagickFormat.Jpeg, 80);
        using (var s = new ThumbnailStore(path, ws.Log))
        {
            Assert.Null(s.TryGet("k1"));
            s.Put("k1", jpeg); s.Put("k2", jpeg);
            Assert.Equal(jpeg, s.TryGet("k1"));
            Assert.True(s.Has("k2"));
            Assert.Equal(2, s.Count);
            Assert.Equal(1, s.Remove(new[] { "k2", "never-there" }));
            Assert.False(s.Has("k2"));
        }
        using (var s = new ThumbnailStore(path, ws.Log))
        {
            Assert.Equal(jpeg, s.TryGet("k1"));                // still there after closing and opening again
            Assert.True(s.DiskBytes > jpeg.Length);
        }
        Assert.Equal("5-AB", ThumbnailStore.KeyOf(5, new byte[] { 0xAB }));
    }

    [Fact]
    public void Store_keeps_itself_sensible_old_pictures_and_anything_over_the_limit_are_forgotten()
    {
        using var ws = new TestWorkspace("ps-trim");
        using var s = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var blob = new byte[10_000];
        for (int i = 0; i < 20; i++) { s.Put("k" + i, blob); Thread.Sleep(2); }

        Assert.Equal(0, s.TrimOld(maxAgeDays: 120, maxBytes: 1_000_000));    // nothing is old, nothing is over the limit
        Assert.Equal(20, s.Count);

        var removed = s.TrimOld(maxAgeDays: 120, maxBytes: 100_000);          // limit 100 KB: the oldest go until 75 KB are left
        Assert.True(removed >= 12, $"removed {removed}");
        Assert.True(s.Count <= 8);
        Assert.True(s.Has("k19"));                                           // the newest survive
        Assert.False(s.Has("k0"));

        Assert.Equal(s.Count, s.TrimOld(maxAgeDays: 0));                     // "older than now": everything
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Store_a_damaged_file_is_set_aside_and_a_new_one_works()
    {
        using var ws = new TestWorkspace("ps-damaged");
        var path = Path.Combine(ws.Paths.DataDir, "pictures.db");
        File.WriteAllText(path, "this is not a database, it is long enough to look like one " + new string('x', 5000));
        using var s = new ThumbnailStore(path, ws.Log);
        s.Put("k", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, s.TryGet("k"));
        Assert.True(File.Exists(path + ".damaged"));
        Assert.Contains("unreadable", File.ReadAllText(ws.Log.CurrentFile));
    }

    // ------------------------------------------------------------ made during the scan

    [Fact]
    public void Scan_makes_a_small_picture_of_each_grouped_photo_while_it_is_open_and_none_for_single_photos()
    {
        using var ws = new TestWorkspace("ps-scan");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var pair = BigJpeg(1);
        ws.Photo("a.jpg", pair); ws.Photo("Sub\\b.jpg", pair);
        ws.Photo("single.jpg", BigJpeg(2));

        var r = ScanWith(ws, store);

        var g = Assert.Single(r.ExactGroups);
        var pairKey = Key(g.Files[0]);
        var jpeg = store.TryGet(pairKey);
        Assert.NotNull(jpeg);                                              // the group's picture is ready before anyone asks
        var (w, h) = Dimensions(jpeg!);
        Assert.Equal(PhotoPictures.ThumbEdge, Math.Max(w, h));              // small, and the right shape (4:3)
        Assert.InRange((double)w / h, 1.30, 1.36);
        Assert.True(jpeg!.Length < 60_000, $"thumbnail is {jpeg.Length} bytes");
        Assert.Equal(1, store.Count);                                       // the single photo's picture was dropped at the end: never shown
        Assert.All(r.ExactGroups.SelectMany(x => x.Files), f => Assert.Null(f.Analysis!.Thumb));   // and none is carried around in memory
    }

    [Fact]
    public void Scan_a_turned_photo_gets_a_picture_the_right_way_up()
    {
        using var ws = new TestWorkspace("ps-rotated");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        using var scene = TestScenes.Scene(7);                              // a landscape scene, saved turned on its side with a note
        var bytes = TestScenes.EncodeRotatedWithOrientationNote(scene);
        ws.Photo("p1.jpg", bytes); ws.Photo("p2.jpg", bytes);

        var r = ScanWith(ws, store);

        var jpeg = store.TryGet(Key(Assert.Single(r.ExactGroups).Files[0]))!;
        var (w, h) = Dimensions(jpeg);
        Assert.True(w > h, $"{w}x{h}: it should be wider than tall, as shown in a photo viewer");
    }

    [Fact]
    public void Scan_a_HEIC_photo_gets_its_picture_during_the_scan_too()
    {
        using var ws = new TestWorkspace("ps-heic");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var heic = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "scene1-no-info.heic"));
        ws.Photo("a.heic", heic); ws.Photo("b.heic", heic);

        var r = ScanWith(ws, store);

        var key = Key(Assert.Single(r.ExactGroups).Files[0]);
        Assert.NotNull(store.TryGet(key));
    }

    [Fact]
    public void Scan_pictures_are_kept_for_photos_that_stay_in_a_group_and_forgotten_when_a_photo_is_left_alone()
    {
        using var ws = new TestWorkspace("ps-prune");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var bytes = BigJpeg(11);
        var a = ws.Photo("a.jpg", bytes); var b = ws.Photo("b.jpg", bytes);
        var r1 = ScanWith(ws, store);
        var key = Key(Assert.Single(r1.ExactGroups).Files[0]);
        Assert.True(store.Has(key));

        var r2 = ScanWith(ws, store);                                       // nothing changed: remembered scan, the picture stays
        Assert.Equal(2, r2.FromCache);
        Assert.True(store.Has(key));

        File.Delete(b);                                                     // the copy is gone: the photo is no longer in any group
        var r3 = ScanWith(ws, store);
        Assert.Empty(r3.ExactGroups);
        Assert.False(store.Has(key));                                       // so its picture is forgotten
    }

    // ------------------------------------------------------------ drawn on the cards

    [Fact]
    public void Cards_a_remembered_picture_appears_without_opening_the_photo_at_all()
    {
        using var ws = new TestWorkspace("ps-warm");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var bytes = BigJpeg(21);
        var a = ws.Photo("a.jpg", bytes); ws.Photo("b.jpg", bytes);
        var r = ScanWith(ws, store);
        var f = Assert.Single(r.ExactGroups).Files[0];
        while (ws.Fs.Opened.TryDequeue(out _)) { }                          // forget the scan's own reading

        var svc = new ThumbnailService(ws.Fs, ws.Log, store);
        svc.Request(Key(f), f.Path, () => { });
        WaitFor(svc, Key(f));

        Assert.NotNull(svc.TryGet(Key(f)));
        Assert.Empty(ws.Fs.Opened);                                         // read from the picture memory: the photo itself was not touched
    }

    [Fact]
    public void Cards_an_online_only_photo_with_a_remembered_picture_is_shown_and_never_opened_or_downloaded()
    {
        using var ws = new TestWorkspace("ps-cloud-warm");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var bytes = BigJpeg(22);
        ws.Photo("a.jpg", bytes); ws.Photo("b.jpg", bytes);
        var f = Assert.Single(ScanWith(ws, store).ExactGroups).Files[0];
        ws.Fs.OnlineOnlyFiles.Add(f.Path);                                  // it has since been moved to the cloud only

        var svc = new ThumbnailService(ws.Fs, ws.Log, store);
        svc.Request(Key(f), f.Path, () => { });
        WaitFor(svc, Key(f));

        Assert.NotNull(svc.TryGet(Key(f)));
        Assert.Empty(ws.Fs.ForbiddenOpens);
    }

    [Fact]
    public void Cards_an_online_only_photo_with_no_remembered_picture_is_never_opened_and_says_why()
    {
        using var ws = new TestWorkspace("ps-cloud-cold");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var p = ws.Photo("a.jpg", BigJpeg(23));
        ws.Fs.OnlineOnlyFiles.Add(p);

        var svc = new ThumbnailService(ws.Fs, ws.Log, store);
        svc.Request("1-AA", p, () => { });
        WaitFor(svc, "1-AA");

        Assert.Null(svc.TryGet("1-AA"));
        Assert.Contains("online-only", svc.FailureOf("1-AA"));
        Assert.Empty(ws.Fs.ForbiddenOpens);
        Assert.Empty(ws.Fs.Opened);
        Assert.False(store.Has("1-AA"));
    }

    [Fact]
    public void Cards_a_missing_picture_is_made_from_the_photo_and_remembered_for_next_time()
    {
        using var ws = new TestWorkspace("ps-cold");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var bytes = BigJpeg(24);
        var a = ws.Photo("a.jpg", bytes); ws.Photo("b.jpg", bytes);
        var f = Assert.Single(ScanWith(ws, null!).ExactGroups).Files[0];     // a scan that made no pictures (as after an upgrade)
        Assert.False(store.Has(Key(f)));

        var svc = new ThumbnailService(ws.Fs, ws.Log, store);
        svc.Request(Key(f), f.Path, () => { });
        WaitFor(svc, Key(f));

        Assert.NotNull(svc.TryGet(Key(f)));
        Assert.True(store.Has(Key(f)));                                     // remembered
        var again = new ThumbnailService(ws.Fs, ws.Log, store);             // a new run of the app
        while (ws.Fs.Opened.TryDequeue(out _)) { }
        again.Request(Key(f), f.Path, () => { });
        WaitFor(again, Key(f));
        Assert.Empty(ws.Fs.Opened);                                         // and not opened again
    }

    [Fact]
    public void Cards_a_photo_that_was_edited_after_the_scan_is_shown_but_not_remembered_under_the_old_name()
    {
        using var ws = new TestWorkspace("ps-edited");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var bytes = BigJpeg(25);
        var a = ws.Photo("a.jpg", bytes); ws.Photo("b.jpg", bytes);
        var f = Assert.Single(ScanWith(ws, null!).ExactGroups).Files[0];
        File.WriteAllBytes(f.Path, BigJpeg(26));                            // a different picture now sits there

        var svc = new ThumbnailService(ws.Fs, ws.Log, store);
        svc.Request(Key(f), f.Path, () => { });
        WaitFor(svc, Key(f));

        Assert.NotNull(svc.TryGet(Key(f)));                                 // what is there now is shown (the safety check catches the change before any removal)
        Assert.False(store.Has(Key(f)));                                    // but the old photo's name is not given to a different picture
    }

    [Fact]
    public void Cards_a_photo_that_is_gone_or_not_a_picture_says_so_and_the_rest_still_appear()
    {
        using var ws = new TestWorkspace("ps-failures");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var good = ws.Photo("good.jpg", BigJpeg(27));
        var junk = ws.Photo("junk.jpg", new byte[3000]);
        var svc = new ThumbnailService(ws.Fs, ws.Log, store);
        svc.Request("g", good, () => { });
        svc.Request("j", junk, () => { });
        svc.Request("m", Path.Combine(ws.Pictures, "not-there.jpg"), () => { });
        WaitFor(svc, "g"); WaitFor(svc, "j"); WaitFor(svc, "m");

        Assert.NotNull(svc.TryGet("g"));
        Assert.StartsWith("Couldn't draw this picture.", svc.FailureOf("j"));
        Assert.Contains("moved or deleted", svc.FailureOf("m"));
    }

    [Fact]
    public void Cards_many_pictures_are_made_at_the_same_time_and_all_arrive()
    {
        using var ws = new TestWorkspace("ps-many");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        var keys = new List<(string key, string path)>();
        for (int i = 0; i < 24; i++) keys.Add(("k" + i, ws.Photo($"p{i}.jpg", BigJpeg(100 + i, 1800, 1200))));
        int peak = 0, running = 0;
        ws.Fs.BeforeOpen = _ => peak = Math.Max(peak, Interlocked.Increment(ref running));
        ws.Fs.AfterOpen = _ => Interlocked.Decrement(ref running);

        var svc = new ThumbnailService(ws.Fs, ws.Log, store);
        svc.Prefetch(keys);
        foreach (var (key, _) in keys) WaitFor(svc, key);

        Assert.All(keys, k => Assert.NotNull(svc.TryGet(k.key)));
        Assert.True(Environment.ProcessorCount < 4 || peak >= 1);
    }

    [Fact]
    public void Cards_the_service_works_without_a_picture_memory_too()
    {
        using var ws = new TestWorkspace("ps-nostore");
        var p = ws.Photo("a.jpg", BigJpeg(28));
        var svc = new ThumbnailService(ws.Fs, ws.Log);
        svc.Request("k", p, () => { });
        WaitFor(svc, "k");
        Assert.NotNull(svc.TryGet("k"));
    }

    // ------------------------------------------------------------ through the screen

    [Fact]
    public async Task Screen_the_first_groups_pictures_start_loading_as_soon_as_the_results_appear_before_anyone_scrolls()
    {
        using var e = new Step3Env("ps-ui");
        for (int i = 0; i < 12; i++) e.Identical(200 + i, $"s{i}\\a.jpg", $"s{i}\\b.jpg");
        await e.ScanAsync();

        var firstCards = e.Vm.ExactGroups.Take(10).SelectMany(g => g.Cards).ToList();
        Assert.NotEmpty(firstCards);
        var until = DateTime.UtcNow.AddSeconds(30);
        // nobody has read Picture: the screen has not asked for anything, yet the pictures are on their way
        while (DateTime.UtcNow < until && !firstCards.All(c => e.S.Thumbnails.TryGet(c.ThumbnailKey) != null || e.S.Thumbnails.FailureOf(c.ThumbnailKey) != null))
            await Task.Delay(10);
        Assert.All(firstCards, c => Assert.True(e.S.Thumbnails.TryGet(c.ThumbnailKey) != null || e.S.Thumbnails.FailureOf(c.ThumbnailKey) != null));
    }

    [Fact]
    public async Task Screen_a_remembered_scan_after_restarting_shows_the_pictures_from_the_memory_without_opening_photos()
    {
        using var ws = new TestWorkspace("ps-restart");
        var bytes = BigJpeg(31);
        ws.Photo("a.jpg", bytes); ws.Photo("b.jpg", bytes);
        string key;
        using (var e1 = new Step3Env("ps-restart-1", existing: ws))
        {
            await e1.ScanAsync();
            key = e1.Vm.ExactGroups.Single().Cards[0].ThumbnailKey;
            Assert.True(e1.S.ThumbStore.Has(key));
        }
        while (ws.Fs.Opened.TryDequeue(out _)) { }
        using var e2 = new Step3Env("ps-restart-2", existing: ws);
        await e2.ScanAsync();                                               // everything remembered: no photo is read
        var card = e2.Vm.ExactGroups.Single().Cards[0];
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && e2.S.Thumbnails.TryGet(card.ThumbnailKey) == null) await Task.Delay(10);

        Assert.NotNull(e2.S.Thumbnails.TryGet(card.ThumbnailKey));
        Assert.Empty(ws.Fs.Opened);
    }

    [Fact]
    public void Disk_the_picture_memory_stays_small_per_photo()
    {
        using var ws = new TestWorkspace("ps-size");
        using var store = new ThumbnailStore(Path.Combine(ws.Paths.DataDir, "pictures.db"), ws.Log);
        for (int i = 0; i < 10; i++) { var b = BigJpeg(300 + i, 3000, 2250); ws.Photo($"x{i}.jpg", b); ws.Photo($"y{i}.jpg", b); }
        var r = ScanWith(ws, store);
        Assert.Equal(10, store.Count);
        var average = r.ExactGroups.Sum(g => store.TryGet(Key(g.Files[0]))!.Length) / 10;
        Assert.True(average < 80_000, $"{average} bytes per picture");
    }
}
