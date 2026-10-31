using System.Windows;
using System.Windows.Media.Imaging;
using ImageMagick;
using Microsoft.Data.Sqlite;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Step 2 on disk and on screen: every kind of copy found by a real scan, shown side by side with the details under each picture.</summary>
[Collection("WPF")]
public class Step2Tests
{
    private static readonly Meta Mine = new("2026:10:12 14:03:09", "07", "Apple", "iPhone 15 Pro", Gps: true);

    /// <summary>Pete's phone photo in every shape it might turn up in, plus look-alikes that must not be grouped with it.</summary>
    internal static void MakeBirthdayPhotos(TestWorkspace ws)
    {
        using var cake = TestScenes.Scene(1);
        var original = TestScenes.Encode(cake, MagickFormat.Jpeg, 92, Mine);
        var first = ws.Photo("Phone import\\IMG_2041.jpg", original);
        File.SetCreationTimeUtc(first, new DateTime(2026, 10, 12, 19, 3, 12, DateTimeKind.Utc));     // the real original: made that afternoon, before the copies
        File.SetLastWriteTimeUtc(first, new DateTime(2026, 10, 12, 19, 3, 12, DateTimeKind.Utc));
        ws.Photo("Desktop backup\\IMG_2041.jpg", original);                                              // identical copy
        ws.Photo("Edited\\IMG_2041 small.jpg", TestScenes.Encode(cake, MagickFormat.Jpeg, 85, Mine, percent: 50));   // resized
        ws.Photo("Edited\\IMG_2041 re-saved.jpg", TestScenes.Encode(cake, MagickFormat.Jpeg, 40, Mine));            // re-saved
        ws.Photo("Converted\\IMG_2041.png", TestScenes.Encode(cake, MagickFormat.Png, meta: Mine));                 // another type
        ws.Photo("Converted\\IMG_2041.heic", File.ReadAllBytes(ImageReaderTests.Fixture("scene1-with-info.heic")));  // iPhone HEIC
        ws.Photo("Sent in chat\\IMG-20261012-WA0003.jpg", TestScenes.ChatAppVersion(cake));                         // info stripped
        ws.Photo("Rotated\\IMG_2041 turned.jpg", TestScenes.EncodeRotatedWithOrientationNote(cake, Mine));          // stored on its side

        // look-alikes that are NOT the same photo
        ws.Photo("Friend's photos\\Cake.jpg", TestScenes.Encode(cake, MagickFormat.Jpeg, 92, Mine with { Make = "samsung", Model = "SM-S918B" }));
        using var burst = TestScenes.Scene(1, subjectShiftPct: 3, noiseSeed: 1);
        ws.Photo("Burst\\IMG_2042.jpg", TestScenes.Encode(burst, MagickFormat.Jpeg, 92, Mine with { SubSec = "41" }));
        using var other = TestScenes.Scene(7);
        ws.Photo("Phone import\\IMG_2077.jpg", TestScenes.Encode(other, MagickFormat.Jpeg, 92, new Meta("2026:10:13 09:00:00", "11", "Apple", "iPhone 15 Pro")));

        // and an unrelated exact pair
        var beach = TestScenes.Encode(TestScenes.Scene(9), MagickFormat.Jpeg, 90, new Meta("2026:08:01 12:00:00", Make: "Apple", Model: "iPhone 15 Pro"));
        ws.Photo("Beach\\IMG_3310.jpg", beach);
        ws.Photo("Beach\\IMG_3310 copy.jpg", beach);
    }

    private static AppServices Services(TestWorkspace ws) => new(ws.Paths, ws.Fs, ws.Policy, ws.Pictures);

    private static async Task<MainViewModel> Scan(AppServices s)
    {
        var vm = WpfHost.Run(() => new MainViewModel(s));
        await WpfHost.Run(() => vm.StartScanAsync());
        return vm;
    }

    // ---------------------------------------------------------------- the scan finds every kind of copy

    [Fact]
    public async Task A_real_scan_groups_every_kind_of_copy_and_keeps_the_look_alikes_out()
    {
        using var ws = new TestWorkspace("s2-scan");
        MakeBirthdayPhotos(ws);
        using var s = Services(ws);

        var vm = await Scan(s);
        var r = vm.Result!;

        Assert.True(r.Completed);
        Assert.Empty(r.Problems);
        var same = Assert.Single(r.SameGroups);
        var names = same.AllFiles.Select(f => f.Path.Substring(ws.Pictures.Length + 1)).OrderBy(x => x).ToList();
        Assert.Equal(new[]
        {
            @"Converted\IMG_2041.heic", @"Converted\IMG_2041.png", @"Desktop backup\IMG_2041.jpg", @"Edited\IMG_2041 re-saved.jpg",
            @"Edited\IMG_2041 small.jpg", @"Phone import\IMG_2041.jpg", @"Rotated\IMG_2041 turned.jpg", @"Sent in chat\IMG-20261012-WA0003.jpg",
        }, names);
        Assert.Equal(8, same.FileCount);
        Assert.Equal(7, same.Versions.Count);   // the two IMG_2041.jpg files are identical copies: one version
        // the friend's shot, the burst neighbour and the unrelated photo are not in any group
        var grouped = r.SameGroups.SelectMany(g => g.AllFiles).Concat(r.ExactGroups.SelectMany(g => g.Files)).Select(f => f.Name).ToList();
        Assert.DoesNotContain("Cake.jpg", grouped);
        Assert.DoesNotContain("IMG_2042.jpg", grouped);
        Assert.DoesNotContain("IMG_2077.jpg", grouped);
        // the unrelated pair is an exact-copy group, on the other tab
        var exact = Assert.Single(r.ExactGroups);
        Assert.Equal(2, exact.Files.Count);
        Assert.Equal(7 + 1, r.DuplicateCount);       // 7 extra in the same-photo group, 1 extra in the exact pair
        Assert.Equal("Exact copies (1)", vm.ExactTabHeader);
        Assert.Equal("Same photo (1)", vm.SameTabHeader);
        Assert.StartsWith("8 duplicates found. You can free ", vm.Headline);
        Assert.Empty(ws.Fs.ForbiddenOpens);
    }

    [Fact]
    public async Task Cards_show_size_pixels_date_taken_folder_and_name_and_mark_identical_copies()
    {
        using var ws = new TestWorkspace("s2-cards");
        MakeBirthdayPhotos(ws);
        using var s = Services(ws);
        var vm = await Scan(s);

        var cards = vm.SameGroups[0].Cards;

        Assert.Equal(8, cards.Count);
        var turned = cards.Single(c => c.Name == "IMG_2041 turned.jpg");
        Assert.Contains("640 × 480", turned.SizeAndDims);                       // its true shape, not the way it is stored
        Assert.Equal("Taken Oct 12, 2026 2:03 PM", turned.TakenText);
        var small = cards.Single(c => c.Name == "IMG_2041 small.jpg");
        Assert.Contains("320 × 240", small.SizeAndDims);
        var chat = cards.Single(c => c.Name == "IMG-20261012-WA0003.jpg");
        Assert.Equal("No date taken", chat.TakenText);
        Assert.Contains("384 × 288", chat.SizeAndDims);
        var heic = cards.Single(c => c.Name == "IMG_2041.heic");
        Assert.Contains("640 × 480", heic.SizeAndDims);
        Assert.Equal(2, cards.Count(c => c.Badge == "Identical copy"));         // the two IMG_2041.jpg
        Assert.Equal(6, cards.Count(c => c.Badge == ""));
        Assert.All(cards, c => { Assert.Contains("KB", c.SizeAndDims + "KB"); Assert.NotEmpty(c.FolderText); Assert.NotEmpty(c.NameText); });
        Assert.Contains("Saved in 7 different ways", vm.SameGroups[0].Subtitle);
    }

    [Fact]
    public async Task The_screen_shows_both_tabs_side_by_side_pictures_and_fits_every_size()
    {
        using var ws = new TestWorkspace("s2-screen");
        MakeBirthdayPhotos(ws);
        using var s = Services(ws);
        var vm = await Scan(s);

        Assert.Equal(0, vm.SelectedTab);   // exact copies first when there are any
        ScreenShots.WaitForPictures(vm.ExactGroups);
        ScreenShots.Take(vm, "step2", "exact-tab");

        vm.SelectedTab = 1;
        ScreenShots.WaitForPictures(vm.SameGroups);
        ScreenShots.Take(vm, "step2", "same-photo-tab", height: 1180);
    }

    [Fact]
    public async Task When_only_same_photo_matches_exist_that_tab_opens_first()
    {
        using var ws = new TestWorkspace("s2-tab");
        using var scene = TestScenes.Scene(1);
        ws.Photo("a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, Mine));
        ws.Photo("b.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 60, Mine, percent: 50));
        using var s = Services(ws);

        var vm = await Scan(s);

        Assert.Equal(1, vm.SelectedTab);
        Assert.Empty(vm.ExactGroups);
        Assert.Equal(Visibility.Visible, vm.ExactEmptyVisibility);
        Assert.Equal("Exact copies (0)", vm.ExactTabHeader);
        ScreenShots.Take(vm, "step2", "only-same");
    }

    // ---------------------------------------------------------------- the cache remembers fingerprints, info and errors

    [Fact]
    public void The_second_scan_reads_no_photo_and_finds_the_same_groups_from_remembered_fingerprints()
    {
        using var ws = new TestWorkspace("s2-cache");
        MakeBirthdayPhotos(ws);
        var first = ws.Scan();
        while (ws.Fs.Opened.TryDequeue(out _)) { }

        var second = ws.Scan();

        Assert.Equal(0, second.ReadFromDisk);
        Assert.Empty(ws.Fs.Opened);
        Assert.Equal(first.SameGroups.Single().FileCount, second.SameGroups.Single().FileCount);
        Assert.Equal(first.ExactGroups.Count, second.ExactGroups.Count);
        var a = first.SameGroups[0].Versions[0].Fingerprint!; var b = second.SameGroups[0].Versions[0].Fingerprint!;
        Assert.Equal(a.PHash, b.PHash); Assert.Equal(a.DHash, b.DHash); Assert.Equal(a.Tiny, b.Tiny);
        Assert.Equal(first.SameGroups[0].Versions[0].Info, second.SameGroups[0].Versions[0].Info);
    }

    [Fact]
    public void A_renamed_photo_keeps_its_fingerprint_without_being_read_again()
    {
        using var ws = new TestWorkspace("s2-moved");
        MakeBirthdayPhotos(ws);
        ws.Scan();
        File.Move(Path.Combine(ws.Pictures, @"Edited\IMG_2041 small.jpg"), Path.Combine(ws.Pictures, @"Edited\tiny version.jpg"));

        var r = ws.Scan();

        Assert.Equal(0, r.ReadFromDisk);
        Assert.Equal(8, Assert.Single(r.SameGroups).FileCount);
        Assert.Contains(r.SameGroups[0].AllFiles, f => f.Name == "tiny version.jpg");
    }

    [Fact]
    public void A_photo_that_cannot_be_opened_as_a_picture_is_listed_every_scan_without_being_read_again_and_still_counts_as_an_exact_copy()
    {
        using var ws = new TestWorkspace("s2-bad");
        var bad = System.Text.Encoding.UTF8.GetBytes("this is not a picture at all, only words");
        ws.Photo("notes.jpg", bad); ws.Photo("notes copy.jpg", bad);
        ws.Photo("fine.jpg", TestPhotoFactory.Jpeg(1));

        var first = ws.Scan();
        Assert.Equal(2, first.Problems.Count);
        Assert.All(first.Problems, p => Assert.Contains("couldn't be opened as a picture", p.Reason));
        Assert.Single(first.ExactGroups);        // two identical files are exact copies even if they are not pictures
        Assert.Empty(first.SameGroups);
        while (ws.Fs.Opened.TryDequeue(out _)) { }

        var second = ws.Scan();

        Assert.Equal(2, second.Problems.Count);  // still listed
        Assert.Empty(ws.Fs.Opened);              // but not read again
        Assert.Contains("notes.jpg", File.ReadAllText(ws.Log.CurrentFile));
    }

    [Fact]
    public void A_cache_made_by_step_1_is_upgraded_and_those_photos_are_read_once_more()
    {
        using var ws = new TestWorkspace("s2-v1");
        var a = ws.Photo("a.jpg", TestPhotoFactory.Jpeg(1));
        ws.Photo("b.jpg", TestPhotoFactory.Jpeg(1));
        // a cache file exactly as step 1 wrote it: version 1, SHA-256 only
        using (var db = new SqliteConnection($"Data Source={ws.Paths.CacheFile};Pooling=False"))
        {
            db.Open();
            foreach (var sql in new[]
            {
                "CREATE TABLE files(path TEXT PRIMARY KEY COLLATE NOCASE, vol INTEGER NOT NULL, fileid INTEGER NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, sha BLOB NOT NULL, seen INTEGER NOT NULL);",
                "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);",
                "INSERT INTO files VALUES('" + a.Replace("'", "''") + "', 1, 1, 100, 1, x'00', 1);",
                "PRAGMA user_version=1;",
            })
            { using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        }

        var cache = ws.Cache;   // opens and upgrades
        var rows = cache.LoadAll();
        Assert.Null(rows[a].Analysis);

        var r = ws.Scan();

        Assert.Equal(2, r.ReadFromDisk);   // both photos read: the old row had no fingerprint
        Assert.Single(r.ExactGroups);
        Assert.NotNull(ws.Cache.LoadAll()[a].Analysis!.Fingerprint);
        Assert.Equal(0, ws.Scan().ReadFromDisk);
    }

    // ---------------------------------------------------------------- pictures on screen: lazy, upright, and honest when they fail

    [Fact]
    public async Task Pictures_are_drawn_upright_only_when_asked_and_a_turned_photo_is_landscape()
    {
        using var ws = new TestWorkspace("s2-thumbs");
        MakeBirthdayPhotos(ws);
        using var s = Services(ws);
        var vm = await Scan(s);
        var cards = vm.SameGroups[0].Cards;
        var turned = cards.Single(c => c.Name == "IMG_2041 turned.jpg");
        Assert.Null(s.Thumbnails.TryGet(turned.ThumbnailKey));   // nothing is drawn until the card is on screen

        ScreenShots.WaitForPictures(vm.SameGroups);

        var img = (BitmapSource)s.Thumbnails.TryGet(turned.ThumbnailKey)!;
        Assert.Equal(PhotoTwins.Core.Imaging.PhotoPictures.ThumbEdge, img.PixelWidth);   // the small card picture: 448 along the long side, still landscape
        Assert.Equal(336, img.PixelHeight);
        var heic = (BitmapSource)s.Thumbnails.TryGet(cards.Single(c => c.Name == "IMG_2041.heic").ThumbnailKey)!;
        Assert.Equal(PhotoTwins.Core.Imaging.PhotoPictures.ThumbEdge, heic.PixelWidth);
        Assert.Empty(ws.Fs.ForbiddenOpens);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_drawn_says_so_on_its_card_and_in_the_log_and_an_online_only_one_is_never_opened()
    {
        using var ws = new TestWorkspace("s2-undrawable");
        var bad = System.Text.Encoding.UTF8.GetBytes("words, not a picture");
        ws.Photo("one.jpg", bad); ws.Photo("two.jpg", bad);
        var good = TestPhotoFactory.StandIn(5);
        var cloud = ws.Photo("c1.jpg", good); ws.Photo("c2.jpg", good);
        using var s = Services(ws);
        var vm = await Scan(s);
        while (ws.Fs.Opened.TryDequeue(out _)) { }   // forget what the scan itself opened
        ws.Fs.BecameOnlineOnly.Add(cloud);           // it goes online-only after the scan, before its picture is drawn

        var groups = vm.ExactGroups.ToList();
        ScreenShots.WaitForPictures(groups);

        var cards = groups.SelectMany(g => g.Cards).ToList();
        var oneCard = cards.First(c => c.Name == "one.jpg");
        Assert.StartsWith("Couldn't draw this picture.", oneCard.PictureStatus);
        var cloudCard = cards.First(c => c.Name == "c1.jpg");
        // identical copies share one picture: either card tells the story, and the cloud file was never opened
        Assert.Empty(ws.Fs.ForbiddenOpens);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == cloud);
        using (var log = new FileStream(s.Log.CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(log))
            Assert.Contains("Couldn't draw", reader.ReadToEnd());
        ScreenShots.Take(vm, "step2", "undrawable");
        _ = cloudCard;
    }

    [Fact]
    public async Task A_photo_deleted_after_the_scan_says_so_instead_of_showing_a_technical_error()
    {
        using var ws = new TestWorkspace("s2-deleted");
        var a = TestPhotoFactory.StandIn(2);
        var pa = ws.Photo("a.jpg", a); ws.Photo("b.jpg", a);
        using var s = Services(ws);
        var vm = await Scan(s);
        File.Delete(pa);   // the test removes its own test photo behind the app's back
        var cards = vm.ExactGroups[0].Cards;

        var viewer = WpfHost.Run(() => new ViewerViewModel(cards, cards.ToList().FindIndex(c => c.Name == "a.jpg"), s));
        await WpfHost.Run(() => viewer.LoadCurrentAsync());

        Assert.Contains("moved or deleted after the scan", viewer.Status);
        Assert.DoesNotContain(@"C:\", viewer.Status);
        var thumb = cards.Single(c => c.Name == "a.jpg");
        ScreenShots.WaitForPictures(vm.ExactGroups);
        Assert.Equal("", thumb.PictureStatus);   // the scan already made its small picture (identical copies share one), so the card still shows it

        // with no remembered picture (as after an upgrade) the card says plainly what happened
        var plain = new ThumbnailService(ws.Fs, ws.Log);
        plain.Request("gone", pa, () => { });
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && plain.FailureOf("gone") == null) Thread.Sleep(10);
        Assert.Contains("moved or deleted", plain.FailureOf("gone"));
    }

    [Fact]
    public async Task Very_long_names_and_deep_folders_wrap_inside_the_cards_and_a_narrow_window_still_fits()
    {
        using var ws = new TestWorkspace("s2-long");
        using var scene = TestScenes.Scene(1);
        var deep = "Family trips and holidays\\2026 summer holiday at the lake with all the cousins\\Day 3 boating and swimming";
        ws.Photo(deep + "\\A_very_long_photo_name_without_any_breaks_in_it_at_all_IMG20261012140309_BURST000_COVER.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, Mine));
        ws.Photo("Short\\IMG20261012140309012345678901234567890123456789.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 50, Mine, percent: 50));
        using var s = Services(ws);
        var vm = await Scan(s);
        Assert.Single(vm.SameGroups);

        ScreenShots.WaitForPictures(vm.SameGroups);
        vm.SelectedTab = 1;
        ScreenShots.Take(vm, "step2", "long-names", width: 1180, height: 780);
        ScreenShots.Take(vm, "step2", "narrow-window", width: 860, height: 900);
    }

    [Fact]
    public void A_twelve_megapixel_photo_is_read_at_reduced_size_but_reports_its_true_size_and_matches_its_small_copy()
    {
        using var big = TestScenes.Scene(3, w: 4032, h: 3024);
        var bigBytes = TestScenes.Encode(big, MagickFormat.Jpeg, 85, Mine);
        var smallBytes = TestScenes.Encode(big, MagickFormat.Jpeg, 80, Mine, percent: 15);
        var analyzer = new PhotoAnalyzer();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = analyzer.Analyze(bigBytes);
        var time = sw.Elapsed;
        var b = analyzer.Analyze(smallBytes);

        Assert.Null(a.Error);
        Assert.Equal(4032, a.Fingerprint!.Width);
        Assert.Equal(3024, a.Fingerprint.Height);
        Assert.True(SamePhoto.FingerprintsMatch(a.Fingerprint, b.Fingerprint!), "a 12-megapixel photo and its 15% copy should look the same");
        Assert.True(time < TimeSpan.FromSeconds(3), $"reading one big photo took {time}");
        var bmp = PhotoPictures.MakeBmp(bigBytes, 640, out int w, out int h);
        Assert.Equal(640, w); Assert.Equal(480, h);
        Assert.True(bmp.Length < 1_500_000);
    }

    // ---------------------------------------------------------------- the full-size viewer

    [Fact]
    public async Task The_viewer_shows_each_photo_full_size_steps_through_the_group_and_never_goes_past_the_ends()
    {
        using var ws = new TestWorkspace("s2-viewer");
        MakeBirthdayPhotos(ws);
        using var s = Services(ws);
        var vm = await Scan(s);
        var cards = vm.SameGroups[0].Cards;
        int start = cards.ToList().FindIndex(c => c.Name == "IMG_2041 turned.jpg");
        var viewer = WpfHost.Run(() => new ViewerViewModel(cards, start, s));

        await WpfHost.Run(() => viewer.LoadCurrentAsync());

        var img = (BitmapSource)viewer.Image!;
        Assert.Equal(640, img.PixelWidth);   // upright, full size
        Assert.Equal(480, img.PixelHeight);
        Assert.Equal("", viewer.Status);
        Assert.Contains("of 8 in this group", viewer.PositionText);
        Assert.Contains("640 × 480", viewer.Details);

        // step to the first and past it
        for (int i = 0; i < 20; i++) await WpfHost.Run(() => viewer.GoAsync(-1));
        Assert.StartsWith("1 of 8", viewer.PositionText);
        Assert.False(viewer.PreviousCommand.CanExecute(null));
        Assert.True(viewer.NextCommand.CanExecute(null));
        for (int i = 0; i < 20; i++) await WpfHost.Run(() => viewer.GoAsync(1));
        Assert.StartsWith("8 of 8", viewer.PositionText);
        Assert.False(viewer.NextCommand.CanExecute(null));

        ScreenShots.TakeViewer(viewer, "step2", "viewer");
        Assert.Empty(ws.Fs.ForbiddenOpens);
    }

    [Fact]
    public async Task Stepping_quickly_ends_on_the_right_photo_and_a_failure_shows_a_message_instead_of_a_blank()
    {
        using var ws = new TestWorkspace("s2-viewer2");
        var bad = System.Text.Encoding.UTF8.GetBytes("words, not a picture");
        ws.Photo("a.jpg", bad); ws.Photo("b.jpg", bad);
        ws.Photo("z.jpg", TestPhotoFactory.StandIn(3)); ws.Photo("y.jpg", TestPhotoFactory.StandIn(3));
        using var s = Services(ws);
        var vm = await Scan(s);
        var cards = vm.ExactGroups.SelectMany(g => g.Cards).ToList();
        int bi = cards.FindIndex(c => c.Name == "a.jpg");
        var viewer = WpfHost.Run(() => new ViewerViewModel(cards, bi, s));

        await WpfHost.Run(() => viewer.LoadCurrentAsync());

        Assert.StartsWith("Couldn't show this picture.", viewer.Status);
        Assert.Null(viewer.Image);

        // rapid clicks: two steps back to back; the picture shown must belong to the last one
        await WpfHost.Run(() => { _ = viewer.GoAsync(1); return viewer.GoAsync(1); });
        await Task.Delay(500);
        Assert.Equal(cards[Math.Min(cards.Count - 1, bi + 2)].Name, viewer.Current.Name);
    }

    [Fact]
    public async Task Viewer_keys_zoom_and_close_work()
    {
        using var ws = new TestWorkspace("s2-keys");
        using var scene = TestScenes.Scene(1);
        ws.Photo("a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, Mine));
        ws.Photo("b.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 50, Mine, percent: 50));
        using var s = Services(ws);
        var vm = await Scan(s);
        var cards = vm.SameGroups[0].Cards;
        var viewer = WpfHost.Run(() => new ViewerViewModel(cards, 0, s));
        await WpfHost.Run(() => viewer.LoadCurrentAsync());

        var result = WpfHost.Run(() =>
        {
            var view = new ViewerView { DataContext = viewer };
            SelfCheck.Host(view, 1100, 760, 1.25);
            view.ApplyZoom();
            double fitW = view.Picture.Width;
            view.ZoomBy(2);
            double zoomedW = view.Picture.Width;
            view.ZoomToFit();
            double backW = view.Picture.Width;
            view.ZoomActual();
            double actualW = view.Picture.Width;
            bool closed = false;
            view.CloseRequested += () => closed = true;
            bool esc = view.HandleKey(System.Windows.Input.Key.Escape);
            bool right = view.HandleKey(System.Windows.Input.Key.Right);
            bool other = view.HandleKey(System.Windows.Input.Key.F5);
            return (fitW, zoomedW, backW, actualW, closed, esc, right, other);
        });

        Assert.True(result.zoomedW > result.fitW * 1.9);
        Assert.Equal(result.fitW, result.backW, 3);
        Assert.Equal(640 / 1.25, result.actualW, 1);   // actual pixels: 640 pixels at 125% scaling is 512 device-independent units
        Assert.True(result.closed && result.esc && result.right);
        Assert.False(result.other);
        await Task.Delay(300);
        Assert.StartsWith("2 of 2", viewer.PositionText);
    }

    [Fact]
    public async Task The_real_viewer_window_builds_and_its_keys_are_wired()
    {
        using var ws = new TestWorkspace("s2-viewwin");
        var a = TestPhotoFactory.StandIn(2);
        ws.Photo("a.jpg", a); ws.Photo("b.jpg", a);
        using var s = Services(ws);
        var vm = await Scan(s);
        var cards = vm.ExactGroups[0].Cards;

        var title = WpfHost.Run(() => new ViewerWindow(cards, 1, s).Model.Heading);

        Assert.Equal("b.jpg", title.Replace("​", ""));
    }

    // ---------------------------------------------------------------- the rules about the code

    [Fact]
    public void S_NET_the_image_reader_is_only_ever_given_bytes_never_a_path_or_web_address()
    {
        var root = Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            var text = File.ReadAllText(f);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"new (?:MagickImage|MagickImageInfo)\(\s*([A-Za-z_][A-Za-z0-9_]*)?"))
                if (m.Groups[1].Success) Assert.True(m.Groups[1].Value is "bytes" or "fileBytes", $"{Path.GetFileName(f)}: {m.Value}");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\bimg\.Read\(\s*([A-Za-z_][A-Za-z0-9_]*)"))
                Assert.True(m.Groups[1].Value is "bytes" or "fileBytes", $"{Path.GetFileName(f)}: {m.Value}");
            Assert.DoesNotContain("MagickImageCollection", text);
        }
    }

    [Fact]
    public void The_memory_budget_makes_big_photos_wait_for_room_and_still_lets_one_huge_photo_through()
    {
        var type = typeof(ScanEngine).Assembly.GetType("PhotoTwins.Core.Scanning.ByteBudget")!;
        var budget = Activator.CreateInstance(type, BindingFlags_All(), null, new object[] { 100L }, null)!;
        var acquire = type.GetMethod("Acquire")!; var release = type.GetMethod("Release")!;
        var control = new ScanControl();

        acquire.Invoke(budget, new object[] { 60L, control });
        var second = Task.Run(() => acquire.Invoke(budget, new object[] { 60L, control }));   // would exceed 100: must wait
        Assert.False(second.Wait(400));
        release.Invoke(budget, new object[] { 60L });
        Assert.True(second.Wait(5000));
        release.Invoke(budget, new object[] { 60L });
        acquire.Invoke(budget, new object[] { 5000L, control });                               // bigger than the whole budget: allowed when nothing else is in
        release.Invoke(budget, new object[] { 5000L });
    }

    private static System.Reflection.BindingFlags BindingFlags_All() =>
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

    [Fact]
    public void Reading_hidden_info_from_garbage_never_throws()
    {
        Assert.True(PhotoInfoReader.Read(new byte[] { 1, 2, 3 }).IsEmpty);
        Assert.True(PhotoInfoReader.Read(Array.Empty<byte>()).IsEmpty);
        Assert.True(PhotoInfoReader.Read(System.Text.Encoding.UTF8.GetBytes("not a photo")).IsEmpty);
    }

    [Fact]
    public void Online_only_photos_are_refused_by_the_picture_reader_before_anything_is_opened()
    {
        using var ws = new TestWorkspace("s2-readonline");
        var p = ws.Photo("c.jpg", TestPhotoFactory.Jpeg(1));
        ws.Fs.OnlineOnlyFiles.Add(p);
        var ex = Assert.Throws<IOException>(() => PhotoPictures.ReadBytes(ws.Fs, p));
        Assert.Contains("online-only", ex.Message);
        Assert.Empty(ws.Fs.Opened);
        Assert.Empty(ws.Fs.ForbiddenOpens);
        var big = Assert.Throws<IOException>(() => PhotoPictures.ReadBytes(ws.Fs, ws.Photo("d.jpg", TestPhotoFactory.StandIn(1, 5000)), maxBytes: 100));
        Assert.Contains("too large", big.Message);
    }
}
