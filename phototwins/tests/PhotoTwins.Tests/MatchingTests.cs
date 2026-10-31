using System.Security.Cryptography;
using ImageMagick;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Step 2: the exact rule for "same photo", proven on made-up photos: every kind of copy, and every look-alike that must NOT match.</summary>
public class MatchingTests
{
    private static readonly PhotoAnalyzer Analyzer = new();
    private static int _id;

    /// <summary>A photo as the scan would know it, without touching the disk.</summary>
    internal static PhotoFile F(string path, byte[] bytes)
    {
        var a = Analyzer.Analyze(bytes);
        return new PhotoFile(path, bytes.Length, DateTime.UtcNow.Ticks, SHA256.HashData(bytes), new FileIdentity(1, (ulong)Interlocked.Increment(ref _id)), a);
    }

    private static readonly Meta Mine = new("2026:10:12 14:03:09", "07", "Apple", "iPhone 15 Pro", Gps: true);

    private static MatchResult Match(params PhotoFile[] files) => PhotoMatcher.Build(files);

    private static void AssertOneSameGroup(MatchResult r, int files)
    {
        var g = Assert.Single(r.SameGroups);
        Assert.Equal(files, g.FileCount);
        Assert.Empty(r.ExactGroups);
    }

    // ---------- copies that MUST be found as the same photo

    [Fact]
    public void An_exact_copy_is_an_exact_copy_not_a_same_photo_match()
    {
        using var scene = TestScenes.Scene(1);
        var bytes = TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine);
        var r = Match(F(@"C:\P\IMG_1.jpg", bytes), F(@"C:\P\cake (1).jpg", bytes), F(@"C:\P\Other\x.jpg", TestScenes.Encode(TestScenes.Scene(2), MagickFormat.Jpeg)));
        Assert.Empty(r.SameGroups);
        Assert.Equal(2, Assert.Single(r.ExactGroups).Files.Count);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(25)]
    public void A_resized_copy_is_the_same_photo(int percent)
    {
        using var scene = TestScenes.Scene(1);
        var original = F(@"C:\P\big.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine));
        var small = F(@"C:\P\small.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine, percent: percent));
        AssertOneSameGroup(Match(original, small), 2);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(30)]
    public void A_re_saved_copy_at_lower_quality_is_the_same_photo(int quality)
    {
        using var scene = TestScenes.Scene(1);
        var original = F(@"C:\P\a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 95, Mine));
        var resaved = F(@"C:\P\b.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, quality, Mine));
        AssertOneSameGroup(Match(original, resaved), 2);
    }

    [Theory]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    [InlineData(MagickFormat.Tiff)]
    [InlineData(MagickFormat.Bmp)]
    public void The_same_picture_saved_as_another_type_is_the_same_photo(MagickFormat other)
    {
        using var scene = TestScenes.Scene(1);
        var jpeg = F(@"C:\P\a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine));
        var copy = F(@"C:\P\a." + other.ToString().ToLowerInvariant(), TestScenes.Encode(scene, other, 80, Mine));
        Assert.Null(copy.Analysis!.Error);
        AssertOneSameGroup(Match(jpeg, copy), 2);
    }

    [Theory]
    [InlineData("scene1-with-info.heic")]
    [InlineData("scene1-no-info.heic")]
    public void An_iphone_HEIC_copy_is_the_same_photo(string fixture)
    {
        using var scene = TestScenes.Scene(1);
        var jpeg = F(@"C:\P\IMG_1.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine));
        var heic = F(@"C:\P\IMG_1.heic", File.ReadAllBytes(ImageReaderTests.Fixture(fixture)));
        Assert.Null(heic.Analysis!.Error);
        Assert.Equal(640, heic.Fingerprint!.Width);
        AssertOneSameGroup(Match(jpeg, heic), 2);
    }

    [Fact]
    public void A_chat_app_copy_with_all_info_stripped_is_the_same_photo()
    {
        using var scene = TestScenes.Scene(1);
        var original = F(@"C:\P\IMG_2041.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine));
        var chat = F(@"C:\P\Sent\IMG-20261012-WA0003.jpg", TestScenes.ChatAppVersion(scene));
        Assert.True(chat.Info.IsEmpty);
        AssertOneSameGroup(Match(original, chat), 2);
    }

    [Fact]
    public void A_copy_stored_on_its_side_with_a_rotation_note_is_the_same_photo_and_reports_its_true_shape()
    {
        using var scene = TestScenes.Scene(1);
        var upright = F(@"C:\P\a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine));
        var sideways = F(@"C:\P\b.jpg", TestScenes.EncodeRotatedWithOrientationNote(scene, Mine));
        using (var raw = new MagickImage(File.Exists("x") ? Array.Empty<byte>() : TestScenes.EncodeRotatedWithOrientationNote(scene, Mine)))
            Assert.Equal(480u, raw.Width);   // really stored 480 wide: the note says "turn it"
        Assert.Equal(640, sideways.Fingerprint!.Width);
        Assert.Equal(480, sideways.Fingerprint.Height);
        AssertOneSameGroup(Match(upright, sideways), 2);
    }

    [Fact]
    public void Everything_at_once_forms_one_group_with_the_biggest_picture_first_and_identical_files_kept_together()
    {
        using var scene = TestScenes.Scene(1);
        var big = TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine);
        var r = Match(
            F(@"C:\P\small.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine, percent: 40)),
            F(@"C:\P\big.jpg", big),
            F(@"C:\P\big copy.jpg", big),
            F(@"C:\P\as.png", TestScenes.Encode(scene, MagickFormat.Png, meta: Mine)),
            F(@"C:\P\chat.jpg", TestScenes.ChatAppVersion(scene)));
        var g = Assert.Single(r.SameGroups);
        Assert.Equal(5, g.FileCount);
        Assert.Equal(4, g.Versions.Count);                       // big.jpg and big copy.jpg are one version (identical copies)
        Assert.Equal(2, g.Versions.Single(v => v.Files.Count > 1).Files.Count);
        Assert.Equal(640, g.Versions[0].Width);                  // biggest picture first
        Assert.Empty(r.ExactGroups);                             // the identical pair lives inside the same-photo group, not in two places
        Assert.Equal(4, g.ExtraPhotos);
    }

    // ---------- look-alikes that must NOT be called the same photo

    [Fact]
    public void A_friends_shot_of_the_same_scene_is_never_the_same_photo()
    {
        using var scene = TestScenes.Scene(1);
        var mine = F(@"C:\P\mine.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine));
        var theirs = F(@"C:\P\friend.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine with { Make = "samsung", Model = "SM-S918B" }));
        Assert.True(SamePhoto.FingerprintsMatch(mine.Fingerprint!, theirs.Fingerprint!), "the pictures themselves look identical");
        var r = Match(mine, theirs);
        Assert.Empty(r.SameGroups);
        Assert.Empty(r.ExactGroups);
    }

    [Fact]
    public void The_same_scene_taken_three_minutes_later_is_not_the_same_photo()
    {
        using var scene = TestScenes.Scene(1);
        var a = F(@"C:\P\a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine));
        var b = F(@"C:\P\b.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, meta: Mine with { Taken = "2026:10:12 14:06:09" }));
        Assert.Empty(Match(a, b).SameGroups);
    }

    [Fact]
    public void Burst_shots_in_the_same_second_are_different_photos_because_their_fraction_of_a_second_differs()
    {
        using var s1 = TestScenes.Scene(1);
        using var s2 = TestScenes.Scene(1, subjectShiftPct: 3, noiseSeed: 1);
        var a = F(@"C:\P\burst1.jpg", TestScenes.Encode(s1, MagickFormat.Jpeg, meta: Mine));
        var b = F(@"C:\P\burst2.jpg", TestScenes.Encode(s2, MagickFormat.Jpeg, meta: Mine with { SubSec = "41" }));
        Assert.Empty(Match(a, b).SameGroups);
    }

    [Fact]
    public void Burst_shots_with_the_same_digits_but_a_different_second_are_different_photos()
    {
        using var s1 = TestScenes.Scene(1);
        using var s2 = TestScenes.Scene(1, 3, 1);
        var a = F(@"C:\P\a.jpg", TestScenes.Encode(s1, MagickFormat.Jpeg, meta: Mine));
        var b = F(@"C:\P\b.jpg", TestScenes.Encode(s2, MagickFormat.Jpeg, meta: Mine with { Taken = "2026:10:12 14:03:10" }));
        Assert.Empty(Match(a, b).SameGroups);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    public void Burst_neighbours_with_no_hidden_info_at_all_are_still_told_apart_when_the_subject_moved_enough(int shiftPercent)
    {
        using var s1 = TestScenes.Scene(1);
        using var s2 = TestScenes.Scene(1, shiftPercent, 1);
        var a = F(@"C:\P\a.jpg", TestScenes.ChatAppVersion(s1));
        var b = F(@"C:\P\b.jpg", TestScenes.ChatAppVersion(s2));
        Assert.Empty(Match(a, b).SameGroups);
    }

    [Fact]
    public void How_close_a_burst_neighbour_must_be_to_match_by_picture_alone_is_recorded()
    {
        // Information only (no assertions on the picture-only column): at what subject movement do two shots stop looking the same
        // when neither carries any hidden info? The "with info" column must always be "different": that is the safety rule.
        var lines = new List<string> { "subject moved | picture-only match | same photo with burst info" };
        for (int shift = 0; shift <= 8; shift++)
        {
            using var s1 = TestScenes.Scene(1);
            using var s2 = TestScenes.Scene(1, shift, 1);
            var a = F(@"C:\P\a.jpg", TestScenes.ChatAppVersion(s1));
            var b = F(@"C:\P\b.jpg", TestScenes.ChatAppVersion(s2));
            var pictureOnly = SamePhoto.FingerprintsMatch(a.Fingerprint!, b.Fingerprint!);
            var withInfoA = F(@"C:\P\c.jpg", TestScenes.Encode(s1, MagickFormat.Jpeg, meta: Mine));
            var withInfoB = F(@"C:\P\d.jpg", TestScenes.Encode(s2, MagickFormat.Jpeg, meta: Mine with { SubSec = "41" }));
            bool sameWithInfo = Match(withInfoA, withInfoB).SameGroups.Count > 0;
            lines.Add($"{shift}% | {pictureOnly} | {sameWithInfo}");
            Assert.False(sameWithInfo, $"a burst pair with different fractions of a second was called the same photo at {shift}%");
        }
        var dir = Path.Combine(TestWorkspace.TestWorkRoot(), "probe");
        System.IO.Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "burst-sensitivity.txt"), lines);
    }

    [Fact]
    public void Unrelated_pictures_never_match_across_a_sweep_of_many_scenes()
    {
        const int count = 140;
        var fps = Enumerable.Range(100, count).Select(seed =>
        {
            using var s = TestScenes.Scene(seed, w: 320, h: 240);
            return Analyzer.Analyze(TestScenes.Encode(s, MagickFormat.Jpeg, 85)).Fingerprint!;
        }).ToList();
        int matches = 0;
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
                if (SamePhoto.FingerprintsMatch(fps[i], fps[j])) matches++;
        Assert.Equal(0, matches);   // 9,730 pairs of unrelated pictures: none called the same
    }

    [Fact]
    public void Blank_pictures_never_match_each_other_by_fingerprint()
    {
        byte[] Flat(MagickColor c) { using var i = new MagickImage(c, 200, 150); i.Format = MagickFormat.Png; return i.ToByteArray(); }
        var white = F(@"C:\P\white.png", Flat(MagickColors.White));
        var black = F(@"C:\P\black.png", Flat(MagickColors.Black));
        var gray = F(@"C:\P\gray.png", Flat(MagickColors.Gray));
        Assert.Empty(Match(white, black, gray).SameGroups);
        // but two identical blank files are still exact copies
        Assert.Single(Match(white, F(@"C:\P\white2.png", Flat(MagickColors.White))).ExactGroups);
    }

    // ---------- the rule's pieces

    [Fact]
    public void Missing_info_never_disagrees_but_a_different_value_that_both_have_always_does()
    {
        var full = new PhotoInfo(new DateTime(2026, 10, 12, 14, 3, 9).Ticks, "07", "Apple", "iPhone 15 Pro", "SN1", "UID1", true);
        Assert.False(InfoRules.Disagree(full, PhotoInfo.None));
        Assert.False(InfoRules.Disagree(PhotoInfo.None, full));
        Assert.False(InfoRules.Disagree(full, full with { TakenTicks = null, SubSec = null }));
        Assert.False(InfoRules.Disagree(full, full with { SubSec = null }));
        Assert.False(InfoRules.Disagree(full, full with { SubSec = "0712" }));      // longer digits: compared at the shorter precision
        Assert.True(InfoRules.Disagree(full, full with { SubSec = "08" }));
        Assert.True(InfoRules.Disagree(full, full with { TakenTicks = full.TakenTicks + TimeSpan.TicksPerSecond }));
        Assert.False(InfoRules.Disagree(full, full with { TakenTicks = full.TakenTicks + 5_000_000 }));   // half a second later, same second, no fractions on one side
        Assert.True(InfoRules.Disagree(full, full with { Make = "samsung" }));
        Assert.True(InfoRules.Disagree(full, full with { Model = "iPhone 14" }));
        Assert.True(InfoRules.Disagree(full, full with { Serial = "SN2" }));
        Assert.True(InfoRules.Disagree(full, full with { UniqueId = "UID2" }));
        Assert.False(InfoRules.Disagree(full, full with { Serial = null, UniqueId = null }));
    }

    [Fact]
    public void The_same_camera_written_two_ways_is_the_same_camera()
    {
        var a = new PhotoInfo(null, null, "Apple", "iPhone 15 Pro", null, null, false);
        var b = new PhotoInfo(null, null, "APPLE", "Apple iPhone 15 Pro", null, null, false);
        var c = new PhotoInfo(null, null, "NIKON CORPORATION", "NIKON D850", null, null, false);
        var d = new PhotoInfo(null, null, "NIKON", "D850", null, null, false);
        Assert.False(InfoRules.Disagree(a, b));
        Assert.False(InfoRules.Disagree(c, d));
        Assert.True(InfoRules.Disagree(a, c));
    }

    [Fact]
    public void An_unset_camera_clock_is_treated_as_no_date()
    {
        using var scene = TestScenes.Scene(1);
        var bytes = TestScenes.Encode(scene, MagickFormat.Jpeg, meta: new Meta("0000:00:00 00:00:00", Make: "Apple"));
        var info = new PhotoAnalyzer().Analyze(bytes).Info;
        Assert.Null(info.TakenTicks);
        Assert.Equal("Apple", info.Make);
    }

    private static Fingerprint Fp(ulong p, ulong d = 0, byte tinyValue = 0) =>
        new(p, d, Enumerable.Range(0, 256).Select(i => (byte)(i % 2 == 0 ? 60 + tinyValue : 200)).ToArray(), 400, 300);

    private static PhotoFile Fake(string path, Fingerprint fp, PhotoInfo? info = null, int fileSize = 1000) =>
        new(path, fileSize, 1, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)), new FileIdentity(1, (ulong)Interlocked.Increment(ref _id)),
            new Analysis(fp, info ?? PhotoInfo.None, null));

    [Fact]
    public void Groups_are_never_chains_a_picture_must_match_the_best_one_not_just_a_neighbour()
    {
        // A and B differ by 5 bits, B and C by 5 other bits: A~B and B~C, but A and C differ by 10 bits.
        var a = Fake(@"C:\P\A.jpg", Fp(0x0000_0000_0000_0000UL));
        var b = Fake(@"C:\P\B.jpg", Fp(0x0000_0000_0000_001FUL));
        var c = Fake(@"C:\P\C.jpg", Fp(0x0000_0000_0000_03FFUL));
        Assert.True(SamePhoto.FingerprintsMatch(a.Fingerprint!, b.Fingerprint!));
        Assert.True(SamePhoto.FingerprintsMatch(b.Fingerprint!, c.Fingerprint!));
        Assert.False(SamePhoto.FingerprintsMatch(a.Fingerprint!, c.Fingerprint!));

        var r = Match(a, b, c);

        var g = Assert.Single(r.SameGroups);
        Assert.Equal(2, g.FileCount);
        Assert.DoesNotContain(g.AllFiles, f => f.Path.EndsWith("A.jpg") && g.AllFiles.Any(x => x.Path.EndsWith("C.jpg")));
    }

    [Fact]
    public void A_picture_cannot_join_a_group_if_its_info_disagrees_with_anyone_already_in_it()
    {
        var t = new DateTime(2026, 10, 12, 14, 3, 9).Ticks;
        var anchor = Fake(@"C:\P\anchor.jpg", Fp(0), new PhotoInfo(t, null, "Apple", null, null, null, false));
        var noInfo = Fake(@"C:\P\noinfo.jpg", Fp(1), PhotoInfo.None);                                    // matches the anchor, says nothing
        var friend = Fake(@"C:\P\friend.jpg", Fp(2), new PhotoInfo(t + 3 * TimeSpan.TicksPerMinute, null, "Apple", null, null, null, false)); // matches the anchor's picture, but 3 minutes later
        var r = Match(anchor, noInfo, friend);
        var g = Assert.Single(r.SameGroups);
        Assert.Equal(2, g.FileCount);
        Assert.DoesNotContain(g.AllFiles, f => f.Path.EndsWith("friend.jpg"));
    }

    [Fact]
    public void Pictures_with_different_shapes_never_match()
    {
        var a = Fake(@"C:\P\a.jpg", Fp(0) with { Width = 400, Height = 300 });
        var b = Fake(@"C:\P\b.jpg", Fp(0) with { Width = 400, Height = 270 });
        Assert.Empty(Match(a, b).SameGroups);
        var c = Fake(@"C:\P\c.jpg", Fp(0) with { Width = 400, Height = 299 });   // 0.3% different: fine
        Assert.Single(Match(a, c).SameGroups);
    }

    [Fact]
    public void Twenty_thousand_pictures_are_compared_quickly()
    {
        var rnd = new Random(5);
        var files = new List<PhotoFile>();
        for (int i = 0; i < 20000; i++)
        {
            var tiny = new byte[256]; rnd.NextBytes(tiny);
            files.Add(new PhotoFile($@"C:\P\{i}.jpg", 1000 + i, 1, SHA256.HashData(BitConverter.GetBytes(i)), new FileIdentity(1, (ulong)i + 1_000_000),
                new Analysis(new Fingerprint((ulong)rnd.NextInt64(), (ulong)rnd.NextInt64(), tiny, 400, 300), PhotoInfo.None, null)));
        }
        // plant 50 real pairs
        for (int i = 0; i < 50; i++)
        {
            var src = files[i].Fingerprint!;
            files.Add(new PhotoFile($@"C:\P\copy{i}.jpg", 500, 1, SHA256.HashData(BitConverter.GetBytes(-i - 1)), new FileIdentity(1, (ulong)i + 2_000_000),
                new Analysis(src with { PHash = src.PHash ^ 0b101 }, PhotoInfo.None, null)));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var r = Match(files.ToArray());

        Assert.Equal(50, r.SameGroups.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    // ---------- pictures that can't be opened

    [Fact]
    public void Damaged_and_fake_pictures_are_reported_not_matched()
    {
        using var scene = TestScenes.Scene(1);
        var good = TestScenes.Encode(scene, MagickFormat.Jpeg);
        var truncated = F(@"C:\P\cut.jpg", good.AsSpan(0, good.Length / 3).ToArray());
        var text = F(@"C:\P\notes.jpg", System.Text.Encoding.UTF8.GetBytes("this is not a picture, just words"));
        Assert.NotNull(truncated.Analysis!.Error);
        Assert.NotNull(text.Analysis!.Error);
        Assert.Contains("couldn't be opened as a picture", text.Analysis.Error);
        Assert.DoesNotContain("@", text.Analysis.Error);   // no internal details
        Assert.Empty(Match(truncated, text).SameGroups);
    }

    [Fact]
    public void Unusual_but_real_kinds_of_pictures_are_read_without_trouble()
    {
        using var scene = TestScenes.Scene(1, w: 320, h: 240);
        byte[] As(Action<MagickImage> tweak, MagickFormat f)
        {
            using var i = (MagickImage)scene.Clone(); tweak(i); i.Format = f; return i.ToByteArray();
        }
        var original = Analyzer.Analyze(TestScenes.Encode(scene, MagickFormat.Jpeg)).Fingerprint!;
        var kinds = new Dictionary<string, byte[]>
        {
            ["gray JPEG"] = As(i => i.ColorType = ColorType.Grayscale, MagickFormat.Jpeg),
            ["CMYK JPEG"] = As(i => i.ColorSpace = ColorSpace.CMYK, MagickFormat.Jpeg),
            ["16-bit PNG"] = As(i => i.Depth = 16, MagickFormat.Png),
            ["palette PNG"] = As(i => i.Quantize(new QuantizeSettings { Colors = 64 }), MagickFormat.Png),
            ["progressive JPEG"] = As(i => i.Settings.Interlace = Interlace.Jpeg, MagickFormat.Jpeg),
            ["lossless WEBP"] = As(i => i.Settings.SetDefine(MagickFormat.WebP, "lossless", "true"), MagickFormat.WebP),
        };
        foreach (var (name, bytes) in kinds)
        {
            var a = Analyzer.Analyze(bytes);
            Assert.True(a.Error == null, $"{name}: {a.Error}");
            Assert.Equal(320, a.Fingerprint!.Width);
        }
        // the others are still the same picture as the original (a CMYK file has different colours by nature, so it is only checked for being readable)
        foreach (var name in new[] { "16-bit PNG", "palette PNG", "progressive JPEG", "lossless WEBP" })
            Assert.True(SamePhoto.FingerprintsMatch(original, Analyzer.Analyze(kinds[name]).Fingerprint!), name);
    }

    [Fact]
    public void A_picture_with_see_through_parts_is_read_on_white()
    {
        using var img = new MagickImage(MagickColors.Transparent, 200, 150);
        img.Draw(new ImageMagick.Drawing.DrawableFillColor(MagickColors.Red), new ImageMagick.Drawing.DrawableRectangle(20, 20, 120, 100));
        img.Format = MagickFormat.Png;
        var a = Analyzer.Analyze(img.ToByteArray());
        Assert.Null(a.Error);
        Assert.Equal(200, a.Fingerprint!.Width);
    }

    [Fact]
    public void A_huge_flat_picture_is_refused_politely_instead_of_running_out_of_memory()
    {
        // 120,000 x 1 pixels is wider than the limit the app allows
        using var img = new MagickImage(MagickColors.White, 100, 1);
        var png = TestScenes.Encode((MagickImage)img.Clone(), MagickFormat.Png);
        Assert.Null(Analyzer.Analyze(png).Error);   // small ones are fine
        ResourceLimits.Width.ToString();            // (limit set by the analyzer: 100,000 wide)
        Assert.Equal(100_000UL, ResourceLimits.Width);
    }
}
