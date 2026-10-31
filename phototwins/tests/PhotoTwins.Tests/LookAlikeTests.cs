using System.Security.Cryptography;
using ImageMagick;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Step 4: look-alikes (different photos that look alike, like burst shots), off by default, with a strictness setting.</summary>
public class LookAlikeTests
{
    private static int _id;
    private static readonly Meta Mine = new("2026:10:12 14:03:09", "07", "Apple", "iPhone 15 Pro", Gps: true);

    private static PhotoFile P(string path, ulong phash, DateTime? taken = null, byte spread = 40, int w = 400, int h = 300)
    {
        var tiny = new byte[256];
        for (int i = 0; i < 256; i++) tiny[i] = (byte)(100 + (i % 2 == 0 ? spread : -spread));
        var info = taken == null ? PhotoInfo.None : new PhotoInfo(taken.Value.Ticks, null, "Apple", "iPhone", null, null, false);
        return new PhotoFile(path, 1000, 1, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)), new FileIdentity(1, (ulong)Interlocked.Increment(ref _id)),
            new Analysis(new Fingerprint(phash, 0, tiny, w, h), info, null));
    }

    private static readonly DateTime T0 = new(2026, 10, 12, 14, 3, 9);

    private static ulong Bits(int n) => n == 0 ? 0 : (1UL << n) - 1;   // a hash that differs from 0 in exactly n bits

    // ---------- the rule: shapes, time window, bit limit, folder

    [Theory]
    [InlineData(Strictness.Strict, 10, 10, true)]
    [InlineData(Strictness.Strict, 11, 10, false)]
    [InlineData(Strictness.Strict, 10, 11, false)]
    [InlineData(Strictness.Normal, 14, 60, true)]
    [InlineData(Strictness.Normal, 15, 60, false)]
    [InlineData(Strictness.Normal, 14, 61, false)]
    [InlineData(Strictness.Loose, 20, 300, true)]
    [InlineData(Strictness.Loose, 21, 300, false)]
    [InlineData(Strictness.Loose, 20, 301, false)]
    public void With_dates_the_picture_and_time_limits_follow_the_strictness_exactly(Strictness s, int bits, int seconds, bool expected)
    {
        var a = P(@"C:\P\a.jpg", 0, T0);
        var b = P(@"C:\P\b.jpg", Bits(bits), T0.AddSeconds(seconds));
        Assert.Equal(expected, LookAlikes.AreLookAlike(a, b, LookAlikes.RuleFor(s)));
        Assert.Equal(expected, LookAlikes.AreLookAlike(b, a, LookAlikes.RuleFor(s)));   // either way round
    }

    [Theory]
    [InlineData(Strictness.Strict, 6, true)]
    [InlineData(Strictness.Strict, 7, false)]
    [InlineData(Strictness.Normal, 10, true)]
    [InlineData(Strictness.Normal, 11, false)]
    [InlineData(Strictness.Loose, 14, true)]
    [InlineData(Strictness.Loose, 15, false)]
    public void Without_a_date_the_pictures_must_be_much_closer_and_in_the_same_folder(Strictness s, int bits, bool expected)
    {
        var a = P(@"C:\P\Trip\a.jpg", 0);
        var b = P(@"C:\P\Trip\b.jpg", Bits(bits), T0);   // one has a date, one hasn't: counts as "no date"
        Assert.Equal(expected, LookAlikes.AreLookAlike(a, b, LookAlikes.RuleFor(s)));
        var elsewhere = P(@"C:\P\Other\b.jpg", Bits(bits));
        Assert.False(LookAlikes.AreLookAlike(a, elsewhere, LookAlikes.RuleFor(s)));   // different folder: never
    }

    [Fact]
    public void Shapes_must_match_within_five_percent_and_blank_pictures_never_count()
    {
        var rule = LookAlikes.RuleFor(Strictness.Normal);
        Assert.True(LookAlikes.AreLookAlike(P(@"C:\P\a.jpg", 0, T0, w: 400, h: 300), P(@"C:\P\b.jpg", 0, T0, w: 400, h: 289), rule));    // 3.8% taller
        Assert.False(LookAlikes.AreLookAlike(P(@"C:\P\a.jpg", 0, T0, w: 400, h: 300), P(@"C:\P\b.jpg", 0, T0, w: 400, h: 270), rule));   // 10%
        Assert.False(LookAlikes.AreLookAlike(P(@"C:\P\a.jpg", 0, T0, spread: 1), P(@"C:\P\b.jpg", 0, T0, spread: 1), rule));              // blank
    }

    [Fact]
    public void Groups_never_drift_every_member_must_be_close_to_the_best_photo()
    {
        // A~B (12 bits) and B~C (12 other bits), but A and C differ by 24 bits
        var a = P(@"C:\P\a.jpg", 0, T0, w: 4000, h: 3000);   // biggest picture: the anchor
        var b = P(@"C:\P\b.jpg", Bits(12), T0.AddSeconds(5), w: 2000, h: 1500);
        var c = P(@"C:\P\c.jpg", Bits(24), T0.AddSeconds(8), w: 1000, h: 750);
        var rule = LookAlikes.RuleFor(Strictness.Normal);
        Assert.True(LookAlikes.AreLookAlike(a, b, rule));
        Assert.True(LookAlikes.AreLookAlike(b, c, rule));
        Assert.False(LookAlikes.AreLookAlike(a, c, rule));

        var groups = LookAlikes.Build(new[] { a, b, c }, Strictness.Normal);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Count);
        Assert.DoesNotContain(g, f => f.Path.EndsWith("c.jpg") && g.Any(x => x.Path.EndsWith("a.jpg")));
    }

    [Fact]
    public void Strictness_defaults_to_Normal_and_unknown_text_means_Normal()
    {
        Assert.Equal(Strictness.Normal, LookAlikes.Parse(null));
        Assert.Equal(Strictness.Normal, LookAlikes.Parse("whatever"));
        Assert.Equal(Strictness.Strict, LookAlikes.Parse("strict"));
        Assert.Equal(Strictness.Loose, LookAlikes.Parse("Loose"));
    }

    [Fact]
    public void Twenty_thousand_photos_are_compared_quickly()
    {
        var rnd = new Random(9);
        var files = new List<PhotoFile>();
        for (int i = 0; i < 20000; i++) files.Add(P($@"C:\P\{i}.jpg", (ulong)rnd.NextInt64(), T0.AddSeconds(rnd.Next(0, 200000))));
        for (int i = 0; i < 40; i++) files.Add(P($@"C:\P\burst{i}.jpg", files[i].Fingerprint!.PHash ^ 0b1011, files[i].Info.Taken!.Value.AddSeconds(3)));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var groups = LookAlikes.Build(files, Strictness.Normal);

        Assert.True(groups.Count(g => g.Count == 2) >= 40);   // the 40 planted bursts (a stray chance match among 20,000 random hashes is possible)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }

    // ---------- on real scans of made-up photos

    private static RemovalHarness Burst(string name, out string[] paths, Strictness? look = Strictness.Normal)
    {
        var h = new RemovalHarness(name) { LookAlikes = look };
        using var s1 = TestScenes.Scene(1);
        using var s2 = TestScenes.Scene(1, 3, 1);
        using var s3 = TestScenes.Scene(1, 4, 2);
        paths = new[]
        {
            h.Ws.Photo("Burst\\IMG_2041.jpg", TestScenes.Encode(s1, MagickFormat.Jpeg, 92, Mine)),
            h.Ws.Photo("Burst\\IMG_2042.jpg", TestScenes.Encode(s2, MagickFormat.Jpeg, 92, Mine with { SubSec = "41" })),
            h.Ws.Photo("Burst\\IMG_2043.jpg", TestScenes.Encode(s3, MagickFormat.Jpeg, 92, Mine with { Taken = "2026:10:12 14:03:10", SubSec = "02" })),
        };
        return h;
    }

    [Fact]
    public void Off_by_default_a_burst_is_not_grouped_as_look_alikes()
    {
        using var h = Burst("la-off", out _, look: null);
        var r = h.Scan();
        Assert.Empty(r.LookAlikes);
        Assert.Empty(r.Decided);
    }

    [Fact]
    public void Burst_shots_become_a_look_alike_group_and_are_never_counted_as_duplicates()
    {
        using var h = Burst("la-burst", out var paths);
        var r = h.Scan();

        Assert.Empty(r.Decided);                    // not the same photo
        Assert.Equal(0, r.DuplicateCount);          // and never in the headline numbers
        Assert.Equal(0, r.FreeableBytes);
        var g = Assert.Single(r.LookAlikes);
        Assert.Equal(GroupKind.LookAlike, g.Kind);
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(paths.OrderBy(p => p), g.Files.Select(f => f.Path).OrderBy(p => p), StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(g.Keeper);
        Assert.False(g.SomeHaveNoDateInfo);         // that warning is only for same-photo groups
    }

    [Fact]
    public void The_strictness_setting_changes_what_is_grouped()
    {
        using var h = new RemovalHarness("la-strict");
        using var scene = TestScenes.Scene(1);
        using var moved = TestScenes.Scene(1, 3, 1);
        h.Ws.Photo("a.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, Mine));
        h.Ws.Photo("b.jpg", TestScenes.Encode(moved, MagickFormat.Jpeg, 92, Mine with { Taken = "2026:10:12 14:04:09", SubSec = "07" }));   // one minute later

        h.LookAlikes = Strictness.Strict; Assert.Empty(h.Scan().LookAlikes);   // 10 seconds: no
        h.LookAlikes = Strictness.Normal; Assert.Single(h.Scan().LookAlikes);  // 60 seconds: yes
        h.LookAlikes = Strictness.Loose; Assert.Single(h.Scan().LookAlikes);   // 5 minutes: yes
    }

    [Fact]
    public void A_friends_shot_of_the_same_scene_is_a_look_alike_but_not_a_copy()
    {
        using var h = new RemovalHarness("la-friend") { LookAlikes = Strictness.Normal };
        using var scene = TestScenes.Scene(1);
        h.Ws.Photo("mine.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, Mine));
        h.Ws.Photo("friend.jpg", TestScenes.Encode(scene, MagickFormat.Jpeg, 92, Mine with { Make = "samsung", Model = "SM-S918B" }));

        var r = h.Scan();

        Assert.Empty(r.Decided);
        Assert.Equal(2, Assert.Single(r.LookAlikes).Files.Count);
    }

    [Fact]
    public void Unrelated_pictures_are_not_look_alikes_at_Strict_or_Normal_even_when_taken_at_the_same_moment()
    {
        var fps = Enumerable.Range(200, 100).Select(seed =>
        {
            using var s = TestScenes.Scene(seed, w: 320, h: 240);
            var bytes = TestScenes.Encode(s, MagickFormat.Jpeg, 85, Mine);
            return new PhotoFile($@"C:\P\{seed}.jpg", bytes.Length, 1, SHA256.HashData(bytes), new FileIdentity(1, (ulong)seed), new PhotoAnalyzer().Analyze(bytes));
        }).ToList();
        int Pairs(Strictness s)
        {
            var rule = LookAlikes.RuleFor(s);
            int n = 0;
            for (int i = 0; i < fps.Count; i++)
                for (int j = i + 1; j < fps.Count; j++)
                    if (LookAlikes.AreLookAlike(fps[i], fps[j], rule)) n++;
            return n;
        }
        int strict = Pairs(Strictness.Strict), normal = Pairs(Strictness.Normal), loose = Pairs(Strictness.Loose);
        var dir = Path.Combine(TestWorkspace.TestWorkRoot(), "probe"); System.IO.Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "lookalike-false-matches.txt"),
            $"unrelated scene pairs called look-alikes out of 4,950, all taken at the same moment: Strict {strict}, Normal {normal}, Loose {loose}");
        Assert.Equal(0, strict);
        Assert.Equal(0, normal);
        // (Loose is allowed to be generous: it is the setting for "group everything from the same few minutes")
    }

    [Fact]
    public void A_group_keeper_takes_part_and_its_exact_copy_stays_out_of_the_look_alike_group()
    {
        using var h = new RemovalHarness("la-keeper") { LookAlikes = Strictness.Normal };
        using var s1 = TestScenes.Scene(1);
        using var s2 = TestScenes.Scene(1, 3, 1);
        var bytes1 = TestScenes.Encode(s1, MagickFormat.Jpeg, 92, Mine);
        h.Ws.Photo("Burst\\IMG_1.jpg", bytes1);
        h.Ws.Photo("Backup\\IMG_1.jpg", bytes1);                                                       // an exact copy of shot 1
        h.Ws.Photo("Burst\\IMG_2.jpg", TestScenes.Encode(s2, MagickFormat.Jpeg, 92, Mine with { SubSec = "41" }));

        var r = h.Scan();

        var exact = Assert.Single(r.Decided);
        Assert.Equal(GroupKind.Exact, exact.Kind);
        var look = Assert.Single(r.LookAlikes);
        Assert.Equal(2, look.Files.Count);
        Assert.Contains(look.Files, f => f.Path == exact.Keeper.Path);          // the exact group joins through its keeper
        Assert.DoesNotContain(look.Files, f => exact.Others.Any(o => o.Path == f.Path));
    }

    // ---------- removing: one group at a time, others only

    [Fact]
    public void Removing_the_others_of_a_look_alike_group_removes_only_those_photos_and_never_in_bulk()
    {
        using var h = Burst("la-remove", out var paths);
        var g = Assert.Single(h.Scan().LookAlikes);
        var keeperBefore = FileSnapshot.Of(g.Keeper.Path);

        var ex = Assert.Throws<ArgumentException>(() => RemovalPlan.ForAllExactCopies(new[] { g }, h.Roots));   // no bulk route exists for look-alikes
        Assert.Contains("Only exact copies", ex.Message);
        var result = h.Run(RemovalPlan.ForGroup(g, h.Roots));

        Assert.Null(result.Refusal);
        Assert.Equal(2, result.Removed.Count);
        Assert.Equal(keeperBefore, FileSnapshot.Of(g.Keeper.Path));
        Assert.Single(Directory.EnumerateFiles(h.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Where(f => !f.Contains("Duplicates to delete")));
        foreach (var e in result.Removed) Assert.True(h.Mutations.PutBack(e).Ok);
        Assert.Equal(3, Directory.EnumerateFiles(h.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count());
        _ = paths;
    }

    [Fact]
    public void Removing_a_photo_that_is_another_groups_keeper_gives_that_group_a_new_keeper_and_it_stays_a_group()
    {
        using var h = new RemovalHarness("la-crossgroup") { LookAlikes = Strictness.Normal };
        using var s1 = TestScenes.Scene(1);
        using var s2 = TestScenes.Scene(1, 3, 1);
        var bytes1 = TestScenes.Encode(s1, MagickFormat.Jpeg, 92, Mine);
        h.Ws.Photo("Burst\\IMG_1.jpg", bytes1);
        h.Ws.Photo("Backup\\IMG_1.jpg", bytes1);
        // shot 2 is the bigger picture, so it becomes the look-alike keeper and shot 1's group keeper is removed
        using var big = TestScenes.Scene(1, 3, 1, w: 800, h: 600);
        h.Ws.Photo("Burst\\IMG_2.jpg", TestScenes.Encode(big, MagickFormat.Jpeg, 92, Mine with { SubSec = "41" }));
        var r = h.Scan();
        var exact = r.Decided.Single();
        var look = r.LookAlikes.Single();
        Assert.Equal("IMG_2.jpg", look.Keeper.Name);
        var removedKeeper = exact.Keeper;

        var result = h.Run(RemovalPlan.ForGroup(look, h.Roots));

        Assert.Equal(removedKeeper.Path, Assert.Single(result.Removed).OriginalPath, ignoreCase: true);
        exact.RemoveFiles(result.Removed.Select(e => e.OriginalPath));
        Assert.Single(exact.Files);                       // one copy of shot 1 is left, so it is no longer a group
        Assert.False(exact.StillAGroup);
        Assert.NotEqual(removedKeeper.Path, exact.Keeper.Path);
        Assert.True(File.Exists(exact.Keeper.Path));
    }
}
