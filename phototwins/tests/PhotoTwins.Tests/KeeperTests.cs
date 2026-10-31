using System.Security.Cryptography;
using ImageMagick;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>The star: which photo of a group is suggested to keep, and why.</summary>
public class KeeperTests
{
    private static int _id;

    private static PhotoFile P(string path, int w = 4000, int h = 3000, long size = 1_000_000, PhotoInfo? info = null, long createdDay = 100, long writtenDay = 100)
    {
        var fp = new Fingerprint(0, 0, new byte[256], w, h);
        return new PhotoFile(path, size, TimeSpan.FromDays(writtenDay).Ticks + DateTime.UnixEpoch.Ticks, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)),
            new FileIdentity(1, (ulong)Interlocked.Increment(ref _id)), new Analysis(fp, info ?? Full, null), TimeSpan.FromDays(createdDay).Ticks + DateTime.UnixEpoch.Ticks);
    }

    private static readonly PhotoInfo Full = new(new DateTime(2026, 10, 12, 14, 3, 9).Ticks, null, "Apple", "iPhone", null, null, true);
    private static readonly PhotoInfo DateOnly = Full with { HasGps = false };
    private static readonly PhotoInfo Nothing = PhotoInfo.None;

    private static string Best(params PhotoFile[] files) => KeeperRule.Rank(files, _ => false)[0].Path;

    // ---------- 1: the biggest PICTURE, never the biggest file

    [Fact]
    public void The_most_pixels_win_even_when_another_file_is_far_bigger_on_disk()
    {
        var bigPicture = P(@"C:\P\a.jpg", 4000, 3000, size: 2_000_000);
        var bigFile = P(@"C:\P\b.png", 2000, 1500, size: 40_000_000);
        Assert.Equal(bigPicture.Path, Best(bigFile, bigPicture));
        Assert.Equal(bigPicture.Path, Best(bigPicture, bigFile));
    }

    [Fact]
    public void A_converted_PNG_that_is_bigger_on_disk_does_not_beat_the_original_JPG_when_the_pixels_tie()
    {
        var jpg = P(@"C:\P\Phone import\IMG_2041.jpg", 640, 480, size: 75_000);
        var png = P(@"C:\P\Converted\IMG_2041.png", 640, 480, size: 668_000);
        var png2 = P(@"C:\P\IMG_2041.png", 640, 480, size: 668_000);          // not even in a Converted folder: a PNG next to a JPG is a conversion
        Assert.Equal(jpg.Path, Best(png, jpg));
        Assert.Equal(jpg.Path, Best(png2, jpg));
    }

    // ---------- 2: the original over a copy

    [Theory]
    [InlineData("IMG_2041 (1).jpg")]
    [InlineData("IMG_2041 (12).jpg")]
    [InlineData("IMG_2041 copy.jpg")]
    [InlineData("IMG_2041 - Copy.jpg")]
    [InlineData("Copy of IMG_2041.jpg")]
    [InlineData("IMG_2041-edited.jpg")]
    [InlineData("IMG_2041_edited.jpg")]
    [InlineData("edited IMG_2041.jpg")]
    [InlineData("IMG_2041 converted.jpg")]
    [InlineData("IMG_2041~2.jpg")]
    [InlineData("IMG-20261012-WA0003.jpg")]
    [InlineData("received_1234567890.jpeg")]
    [InlineData("10234_5678_90123_n.jpg")]
    public void Names_that_look_like_copies_are_copies(string name) => Assert.True(KeeperRule.IsCopyName(name), name);

    [Theory]
    [InlineData("IMG_2041.jpg")]
    [InlineData("Copyright 2026.jpg")]
    [InlineData("Birthday party.jpg")]
    [InlineData("DSC00123.JPG")]
    [InlineData("Edith.jpg")]
    [InlineData("Credited.jpg")]
    [InlineData("PXL_20261012_140309.jpg")]
    public void Ordinary_names_are_not_copies(string name) => Assert.False(KeeperRule.IsCopyName(name), name);

    [Fact]
    public void Folders_called_Converted_or_Edited_hold_copies()
    {
        Assert.True(KeeperRule.InCopyFolder(@"C:\Pics\Converted\x.jpg"));
        Assert.True(KeeperRule.InCopyFolder(@"C:\Pics\edited\Trip\x.jpg"));
        Assert.True(KeeperRule.InCopyFolder(@"C:\Pics\Edits\x.jpg"));
        Assert.False(KeeperRule.InCopyFolder(@"C:\Pics\Edited trips\x.jpg"));   // only the exact folder names count
        Assert.False(KeeperRule.InCopyFolder(@"C:\Pics\2026\x.jpg"));
    }

    [Fact]
    public void When_pixels_tie_the_original_beats_a_copy_whatever_kind_of_copy_it_is()
    {
        var original = P(@"C:\P\Trip\IMG_2041.jpg", createdDay: 200);   // newer than the copies, yet still the one to keep
        foreach (var copyPath in new[] { @"C:\P\Trip\IMG_2041 (1).jpg", @"C:\P\Edited\IMG_2041.jpg", @"C:\P\Converted\IMG_2041.jpg", @"C:\P\Sent\IMG-20261012-WA0003.jpg", @"C:\P\IMG_2041 copy.jpg" })
            Assert.Equal(original.Path, Best(P(copyPath, createdDay: 10, writtenDay: 10), original));
    }

    [Fact]
    public void A_camera_type_file_is_never_called_a_conversion_but_a_PNG_BMP_TIFF_or_WEBP_beside_one_is()
    {
        var jpg = P(@"C:\P\a.jpg"); var heic = P(@"C:\P\a.heic"); var png = P(@"C:\P\a.png"); var tif = P(@"C:\P\a.tiff"); var webp = P(@"C:\P\a.webp");
        var all = new[] { jpg, heic, png, tif, webp };
        Assert.False(KeeperRule.IsConverted(jpg, all));
        Assert.False(KeeperRule.IsConverted(heic, all));
        Assert.True(KeeperRule.IsConverted(png, all));
        Assert.True(KeeperRule.IsConverted(tif, all));
        Assert.True(KeeperRule.IsConverted(webp, all));
        // with no camera type in the group nothing is a conversion
        var onlyPngAndBmp = new[] { png, P(@"C:\P\b.bmp") };
        Assert.False(KeeperRule.IsConverted(png, onlyPngAndBmp));
    }

    // ---------- 3: date and place info

    [Fact]
    public void When_the_original_question_is_a_tie_the_photo_with_date_and_place_wins_then_date_only_then_neither()
    {
        var both = P(@"C:\P\z1.jpg", info: Full, createdDay: 300);
        var dateOnly = P(@"C:\P\a1.jpg", info: DateOnly, createdDay: 100);
        var neither = P(@"C:\P\a2.jpg", info: Nothing, createdDay: 50);
        Assert.Equal(both.Path, Best(neither, dateOnly, both));
        Assert.Equal(dateOnly.Path, Best(neither, dateOnly));
    }

    // ---------- 4: the oldest

    [Fact]
    public void Then_the_oldest_wins_judged_by_the_earlier_of_created_and_last_changed()
    {
        var older = P(@"C:\P\b.jpg", createdDay: 50, writtenDay: 50);
        var newer = P(@"C:\P\a.jpg", createdDay: 90, writtenDay: 90);
        Assert.Equal(older.Path, Best(newer, older));
        // a file COPIED yesterday keeps its old last-changed date: the earlier of the two dates is what counts
        var copiedYesterday = P(@"C:\P\c.jpg", createdDay: 400, writtenDay: 20);
        Assert.Equal(copiedYesterday.Path, Best(newer, older, copiedYesterday));
    }

    // ---------- 5: stable

    [Fact]
    public void Complete_ties_always_give_the_same_answer_whatever_order_the_photos_arrive_in()
    {
        var a = P(@"C:\P\Deep\Deeper\x.jpg"); var b = P(@"C:\P\x.jpg"); var c = P(@"C:\P\Sub\x.jpg"); var d = P(@"C:\P\y.jpg");
        var expected = Best(a, b, c, d);
        Assert.Equal(@"C:\P\x.jpg", expected);   // fewest folder levels, then alphabetical
        var rnd = new Random(1);
        for (int i = 0; i < 20; i++)
            Assert.Equal(expected, Best(new[] { a, b, c, d }.OrderBy(_ => rnd.Next()).ToArray()));
    }

    // ---------- 0: shared first

    [Fact]
    public void A_shared_copy_always_wins_even_if_it_is_a_small_copy_and_only_shared_photos_compete()
    {
        var bigOriginal = P(@"C:\Mine\IMG_2041.jpg", 4000, 3000);
        var tinySharedCopy = P(@"C:\Family\IMG_2041 (1).jpg", 400, 300, size: 30_000);
        var sharedBigger = P(@"C:\Family2\IMG_2041.jpg", 800, 600, size: 90_000);
        bool Shared(PhotoFile f) => f.Path.StartsWith(@"C:\Family", StringComparison.Ordinal);
        Assert.Equal(tinySharedCopy.Path, KeeperRule.Rank(new[] { bigOriginal, tinySharedCopy }, Shared)[0].Path);
        Assert.Equal(sharedBigger.Path, KeeperRule.Rank(new[] { bigOriginal, tinySharedCopy, sharedBigger }, Shared)[0].Path);   // among shared: the rule applies as usual
    }

    // ---------- on real scans: what the user will see

    [Fact]
    public void In_the_step_2_birthday_group_the_original_JPG_gets_the_star_not_the_bigger_PNG()
    {
        using var h = new RemovalHarness("k-birthday");
        Step2Tests.MakeBirthdayPhotos(h.Ws);
        var scan = h.Scan();
        var g = scan.Decided.Single(x => x.Kind == GroupKind.Same);

        var png = g.Files.Single(f => f.Name == "IMG_2041.png");
        var jpgs = g.Files.Where(f => f.Name == "IMG_2041.jpg").ToList();
        Assert.True(png.Size > jpgs[0].Size, "the PNG really is the bigger file");
        Assert.Equal("IMG_2041.jpg", g.Keeper.Name);
        Assert.NotEqual(png.Path, g.Keeper.Path);
        Assert.Equal(g.Files[0].Path, g.Keeper.Path);                       // keeper is listed first
        Assert.True(KeeperRule.LooksLikeACopy(png, g.Files));
        Assert.True(KeeperRule.LooksLikeACopy(g.Files.Single(f => f.Name == "IMG_2041.heic"), g.Files));   // sits in a Converted folder
        Assert.True(KeeperRule.LooksLikeACopy(g.Files.Single(f => f.Name == "IMG_2041 re-saved.jpg"), g.Files));   // in an Edited folder
        Assert.Equal(jpgs.Single(j => j.Path.Contains("Phone import")).Path, g.Keeper.Path);   // two identical originals: the older one
    }

    [Fact]
    public void A_keeper_Pete_picked_is_remembered_across_scans_but_never_off_a_shared_copy()
    {
        using var h = new RemovalHarness("k-picks");
        h.Identical(1, "a.jpg", "b.jpg");
        var g = h.Scan().Decided.Single();
        var other = g.Others.Single();
        Assert.True(g.TrySetKeeper(other));
        h.Picks[g.Key] = other.Path;

        var again = h.Scan().Decided.Single();

        Assert.Equal(g.Key, again.Key);                    // the group keeps its name
        Assert.Equal(other.Path, again.Keeper.Path);       // and the user's pick
        Assert.True(again.KeeperChosenByUser);

        // a pick that no longer exists falls back to the rule
        h.Picks[g.Key] = Path.Combine(h.Ws.Pictures, "gone.jpg");
        Assert.False(h.Scan().Decided.Single().KeeperChosenByUser);

        // a pick of a non-shared copy is ignored when the group has a shared copy
        h.SharedMarked.Add(Path.Combine(h.Ws.Pictures, "a.jpg"));
        h.Picks[g.Key] = Path.Combine(h.Ws.Pictures, "b.jpg");
        var shared = h.Scan().Decided.Single();
        Assert.Equal(Path.Combine(h.Ws.Pictures, "a.jpg"), shared.Keeper.Path, ignoreCase: true);
    }

    // ---------- the missing-info warning

    [Fact]
    public void A_same_photo_group_where_some_photo_has_no_date_taken_is_flagged_and_exact_groups_never_are()
    {
        using var h = new RemovalHarness("k-nodate");
        using var scene = TestScenes.Scene(1);
        var mine = new Meta("2026:10:12 14:03:09", "07", "Apple", "iPhone 15 Pro", Gps: true);
        h.Ws.Photo("with-date.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, mine));
        h.Ws.Photo("chat.jpg", TestScenes.ChatAppVersion(scene));
        var exact = TestPhotoFactory.StandIn(50);
        h.Ws.Photo("e1.jpg", exact); h.Ws.Photo("e2.jpg", exact);   // exact copies without any date info

        var decided = h.Scan().Decided;

        Assert.True(decided.Single(g => g.Kind == GroupKind.Same).SomeHaveNoDateInfo);
        Assert.False(decided.Single(g => g.Kind == GroupKind.Exact).SomeHaveNoDateInfo);

        // and not flagged when every photo has a date
        using var h2 = new RemovalHarness("k-alldates");
        h2.Ws.Photo("a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, mine));
        h2.Ws.Photo("b.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 60, mine, percent: 50));
        Assert.False(h2.Scan().Decided.Single().SomeHaveNoDateInfo);
    }

    [Fact]
    public void The_headline_numbers_use_the_real_keeper()
    {
        using var h = new RemovalHarness("k-numbers");
        using var scene = TestScenes.Scene(1);
        h.Ws.Photo("big.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 95));
        h.Ws.Photo("small.png", TestScenes.Encode(scene, MagickFormat.Png, percent: 50));
        h.Ws.Photo("copy.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 95));   // identical to big.jpg

        var r = h.Scan();
        var g = r.Decided.Single();

        Assert.Equal(2, r.DuplicateCount);
        Assert.Equal(g.Others.Sum(f => f.Size), r.FreeableBytes);
        Assert.Equal(g.Files.Sum(f => f.Size) - g.Keeper.Size, r.FreeableBytes);
    }
}
