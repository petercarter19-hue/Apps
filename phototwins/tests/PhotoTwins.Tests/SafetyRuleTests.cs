using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>The scan-side safety rules: S-USERS, S-ONLINE, S-INSIDE, S-SILENT, S-TESTDATA.</summary>
public class SafetyRuleTests
{
    // ---------- S-ONLINE: online-only files are never opened, so never downloaded

    [Fact]
    public void S_ONLINE_online_only_photos_are_skipped_counted_and_never_opened()
    {
        using var ws = new TestWorkspace("online");
        var a = TestPhotoFactory.Jpeg(1);
        ws.Photo("local1.jpg", a); ws.Photo("local2.jpg", a);
        var c1 = ws.Photo("cloud1.jpg", TestPhotoFactory.Jpeg(2));
        var c2 = ws.Photo("cloud2.heic", TestPhotoFactory.StandIn(3));
        ws.Fs.OnlineOnlyFiles.Add(c1); ws.Fs.OnlineOnlyFiles.Add(c2);

        var r = ws.Scan();

        Assert.Equal(2, r.SkippedOnline);
        Assert.Equal(2, r.PhotosFound);
        Assert.Empty(ws.Fs.ForbiddenOpens);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == c1 || p == c2);
        Assert.Single(r.ExactGroups);
    }

    [Fact]
    public void S_ONLINE_a_photo_that_goes_online_only_after_listing_is_still_never_opened()
    {
        using var ws = new TestWorkspace("online-late");
        var a = ws.Photo("a.jpg", TestPhotoFactory.Jpeg(1));
        ws.Photo("b.jpg", TestPhotoFactory.Jpeg(2));
        ws.Fs.BecameOnlineOnly.Add(a);

        var r = ws.Scan();

        Assert.Empty(ws.Fs.ForbiddenOpens);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == a);
        Assert.Equal(1, r.SkippedOnline);
    }

    [Fact]
    public void S_ONLINE_an_online_only_folder_is_left_out_and_nothing_inside_is_opened()
    {
        using var ws = new TestWorkspace("online-dir");
        ws.Photo("here.jpg", TestPhotoFactory.Jpeg(1));
        var inside = ws.Photo("CloudFolder\\x.jpg", TestPhotoFactory.Jpeg(2));
        ws.Fs.OnlineOnlyDirs.Add(Path.Combine(ws.Pictures, "CloudFolder"));

        var r = ws.Scan();

        Assert.Equal(1, r.PhotosFound);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == inside);
        Assert.Contains(r.LeftOut, l => l.Path.EndsWith("CloudFolder") && l.Reason.Contains("online-only"));
    }

    [Theory]
    [InlineData(0x400000, true)]   // recall on data access
    [InlineData(0x40000, true)]    // recall on open
    [InlineData(0x1000, true)]     // offline
    [InlineData(0x401620, true)]   // what OneDrive and iCloud really use for online-only photos on this PC
    [InlineData(0x420, false)]     // a normal OneDrive photo that is stored on this PC
    [InlineData(0x20, false)]
    [InlineData(0x80, false)]
    public void S_ONLINE_the_attribute_bits_mean_what_windows_says(int bits, bool onlineOnly) =>
        Assert.Equal(onlineOnly, CloudAttributes.IsOnlineOnly((FileAttributes)bits));

    // ---------- S-USERS: other people's accounts

    [Fact]
    public void S_USERS_another_persons_account_cannot_be_picked()
    {
        using var ws = new TestWorkspace("users-pick");
        var chk = ScanRoots.Check(ws.Other, ws.Fs, ws.Policy);
        Assert.False(chk.Ok);
        Assert.Contains("another person", chk.Problem);

        var sub = Path.Combine(ws.Other, "Pictures");
        Directory.CreateDirectory(sub);
        Assert.False(ScanRoots.Check(sub, ws.Fs, ws.Policy).Ok);
    }

    [Fact]
    public void S_USERS_scanning_a_parent_of_all_accounts_skips_the_others()
    {
        using var ws = new TestWorkspace("users-walk");
        ws.Photo("mine.jpg", TestPhotoFactory.Jpeg(1));
        var theirs = TestPhotoFactory.Write(Path.Combine(ws.Other, "Pictures", "private.jpg"), TestPhotoFactory.Jpeg(2));
        var users = Path.Combine(ws.Root, "Users");

        var r = ws.Scan(users);

        Assert.Equal(1, r.PhotosFound);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == theirs);
        Assert.Contains(r.LeftOut, l => l.Path == ws.Other && l.Reason.Contains("another person"));
    }

    [Fact]
    public void S_USERS_the_real_pc_list_of_accounts_does_not_include_me_but_does_include_others()
    {
        var policy = ScanPolicy.ForThisPc();
        var me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(policy.IsInsideOtherProfile(me));
        Assert.False(policy.IsInsideOtherProfile(Path.Combine(me, "Pictures")));
        // The other account on this PC (WsiAccount) is recognised without scanning anything in it.
        var users = Path.GetDirectoryName(me)!;
        var others = Directory.EnumerateDirectories(users).Where(d => !d.Equals(me, StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(d).Equals("Public", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var o in others) Assert.True(policy.IsInsideOtherProfile(Path.Combine(o, "Pictures")), o);
    }

    [Fact]
    public void S_USERS_programs_own_files_folder_inside_my_profile_is_left_alone()
    {
        using var ws = new TestWorkspace("appdata");
        ws.Photo("keep.jpg", TestPhotoFactory.Jpeg(1));
        var junk = TestPhotoFactory.Write(Path.Combine(ws.Me, "AppData", "Roaming", "x", "icon.png"), TestPhotoFactory.Png(2));

        var r = ws.Scan(ws.Me);

        Assert.Equal(1, r.PhotosFound);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == junk);
    }

    // ---------- Google Drive streamed drive

    [Fact]
    public void S_ONLINE_google_drive_is_never_read_and_the_reason_is_shown()
    {
        using var ws = new TestWorkspace("gdrive");
        var inside = TestPhotoFactory.Write(Path.Combine(ws.FakeGoogleDrive, "My Drive", "a.jpg"), TestPhotoFactory.Jpeg(1));

        var chk = ScanRoots.Check(Path.Combine(ws.FakeGoogleDrive, "My Drive"), ws.Fs, ws.Policy);
        Assert.False(chk.Ok);
        Assert.Contains("Google Drive", chk.Problem);

        var r = ws.Scan(Path.Combine(ws.FakeGoogleDrive, "My Drive"));
        Assert.Equal(0, r.PhotosFound);
        Assert.Single(r.LeftOut);
        Assert.Empty(ws.Fs.Opened);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == inside);
    }

    // ---------- S-INSIDE: never leave the picked folders

    [Fact]
    public void S_INSIDE_folder_links_are_not_followed_out_of_the_picked_folder()
    {
        using var ws = new TestWorkspace("junction");
        ws.Photo("mine.jpg", TestPhotoFactory.Jpeg(1));
        var outside = TestPhotoFactory.Write(Path.Combine(ws.Outside, "elsewhere.jpg"), TestPhotoFactory.Jpeg(2));
        ws.MakeJunction(Path.Combine(ws.Pictures, "shortcut to outside"), ws.Outside);

        var r = ws.Scan();

        Assert.Equal(1, r.PhotosFound);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == outside);
        Assert.Contains(r.LeftOut, l => l.Path.EndsWith("shortcut to outside") && l.Reason.Contains("shortcut link"));
    }

    [Fact]
    public void S_INSIDE_links_nested_two_levels_down_are_not_followed_either()
    {
        using var ws = new TestWorkspace("junction-deep");
        ws.Photo("Trips\\mine.jpg", TestPhotoFactory.Jpeg(1));
        var outside = TestPhotoFactory.Write(Path.Combine(ws.Outside, "Deep", "elsewhere.jpg"), TestPhotoFactory.Jpeg(2));
        ws.MakeJunction(Path.Combine(ws.Pictures, "Trips", "Summer", "link"), Path.Combine(ws.Outside));

        var r = ws.Scan();

        Assert.Equal(1, r.PhotosFound);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == outside);
        Assert.Single(r.LeftOut);
    }

    [Fact]
    public void S_INSIDE_the_duplicates_to_delete_folder_is_not_scanned_and_cannot_be_picked()
    {
        using var ws = new TestWorkspace("holding");
        ws.Photo("keep.jpg", TestPhotoFactory.Jpeg(1));
        var held = ws.Photo("Duplicates to delete\\moved.jpg", TestPhotoFactory.Jpeg(1));

        var r = ws.Scan();

        Assert.Equal(1, r.PhotosFound);
        Assert.Empty(r.ExactGroups);
        Assert.DoesNotContain(ws.Fs.Opened, p => p == held);
        Assert.False(ScanRoots.Check(Path.Combine(ws.Pictures, "Duplicates to delete"), ws.Fs, ws.Policy).Ok);
    }

    [Theory]
    [InlineData(@"C:\Windows\Web")]
    [InlineData(@"C:\Program Files\Common Files")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"D:\System Volume Information")]
    public void S_INSIDE_windows_system_folders_cannot_be_picked(string path)
    {
        var policy = new ScanPolicy(@"C:\Users\me", Array.Empty<string>(), _ => false);
        Assert.NotNull(policy.WhyNotAllowedAsRoot(path));
    }

    [Fact]
    public void Picked_folders_inside_other_picked_folders_are_not_scanned_twice()
    {
        using var ws = new TestWorkspace("nested");
        var a = TestPhotoFactory.Jpeg(1);
        ws.Photo("one.jpg", a); ws.Photo("Sub\\two.jpg", a);

        var r = ws.Scan(ws.Pictures, Path.Combine(ws.Pictures, "Sub"), ws.Pictures.ToUpperInvariant());

        Assert.Equal(2, r.PhotosFound);
        Assert.Single(r.Roots);
        Assert.Equal(2, Assert.Single(r.ExactGroups).Files.Count);
    }

    // ---------- S-SILENT: nothing fails quietly

    [Fact]
    public void S_SILENT_empty_and_locked_photos_are_listed_with_reasons_and_logged()
    {
        using var ws = new TestWorkspace("problems");
        ws.Photo("fine1.jpg", TestPhotoFactory.Jpeg(1)); ws.Photo("fine2.jpg", TestPhotoFactory.Jpeg(1));
        var empty = ws.Photo("empty.jpg", Array.Empty<byte>());
        var locked = ws.Photo("locked.jpg", TestPhotoFactory.Jpeg(5));
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var r = ws.Scan();

        Assert.Equal(2, r.Problems.Count);
        Assert.Contains(r.Problems, p => p.Path == empty && p.Reason.Contains("empty"));
        Assert.Contains(r.Problems, p => p.Path == locked && p.Reason.Contains("Another program"));
        Assert.Single(r.ExactGroups);   // the rest of the scan carried on
        var log = File.ReadAllText(ws.Log.CurrentFile);
        Assert.Contains(empty, log);
        Assert.Contains(locked, log);
    }

    [Fact]
    public void S_SILENT_a_photo_deleted_during_the_scan_is_reported()
    {
        using var ws = new TestWorkspace("vanish");
        var victim = ws.Photo("victim.jpg", TestPhotoFactory.Jpeg(1)); ws.Photo("other.jpg", TestPhotoFactory.Jpeg(2));
        ws.Fs.BeforeOpen = p => { if (p == victim) File.Delete(victim); };

        var r = ws.Scan();

        var prob = Assert.Single(r.Problems);
        Assert.Equal(victim, prob.Path);
        Assert.Contains("moved or deleted", prob.Reason);
        Assert.Contains(victim, File.ReadAllText(ws.Log.CurrentFile));
    }

    [Fact]
    public void S_SILENT_a_photo_changing_while_it_is_read_is_reported_not_trusted()
    {
        using var ws = new TestWorkspace("changing");
        var p = ws.Photo("busy.jpg", TestPhotoFactory.Jpeg(1, 300, 200));
        ws.Photo("calm.jpg", TestPhotoFactory.Jpeg(2));
        // another program adds bytes to the photo right after the scan opens it
        ws.Fs.AfterOpen = path =>
        {
            if (path != p) return;
            using var w = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            w.Write(new byte[] { 1, 2, 3 });
        };

        var r = ws.Scan();

        var prob = Assert.Single(r.Problems);
        Assert.Equal(p, prob.Path);
        Assert.Contains("changed while", prob.Reason);
        Assert.Equal(1, r.PhotosFound - r.Problems.Count);   // the calm photo was fine
        Assert.Equal(1, ws.Cache.RowCount);                  // the changing one was not remembered
    }

    [Fact]
    public void S_SILENT_a_folder_windows_refuses_is_reported()
    {
        using var ws = new TestWorkspace("denied");
        ws.Photo("ok.jpg", TestPhotoFactory.Jpeg(1));
        var fs = new ThrowingFolderFileSystem(ws.Fs, Path.Combine(ws.Pictures, "Secret"));
        Directory.CreateDirectory(Path.Combine(ws.Pictures, "Secret"));
        var engine = new ScanEngine(fs, ws.Cache, ws.Log, ws.Policy);

        var r = engine.Run(new[] { ws.Pictures }, new ScanControl(), null);

        Assert.Equal(1, r.PhotosFound);
        var prob = Assert.Single(r.Problems);
        Assert.Contains("Couldn't open this folder", prob.Reason);
        Assert.Contains("permission", prob.Reason);
    }

    private sealed class ThrowingFolderFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner; private readonly string _bad;
        public ThrowingFolderFileSystem(IFileSystem inner, string bad) { _inner = inner; _bad = bad; }
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public IEnumerable<FileEntry> Enumerate(string directory)
        {
            if (string.Equals(directory, _bad, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("denied by the test");
            return _inner.Enumerate(directory);
        }
        public FileAttributes GetAttributes(string path) => _inner.GetAttributes(path);
        public string GetRealPath(string path) => _inner.GetRealPath(path);
        public bool IsLink(string path) => _inner.IsLink(path);
        public OpenedFile OpenRead(string path) => _inner.OpenRead(path);
    }

    // ---------- Cache

    [Fact]
    public void A_damaged_cache_is_set_aside_and_the_scan_still_works()
    {
        using var ws = new TestWorkspace("badcache");
        File.WriteAllText(ws.Paths.CacheFile, "this is not a database at all");
        ws.Photo("a.jpg", TestPhotoFactory.Jpeg(1)); ws.Photo("b.jpg", TestPhotoFactory.Jpeg(1));

        Assert.True(ws.Cache.DamagedAndReset);
        var r = ws.Scan();

        Assert.True(File.Exists(ws.Paths.CacheFile + ".damaged"));
        Assert.Single(r.ExactGroups);
        Assert.Contains("Cache file unreadable", File.ReadAllText(ws.Log.CurrentFile));
    }

    // ---------- S-TESTDATA

    [Fact]
    public void S_TESTDATA_the_test_file_system_refuses_anything_outside_test_work()
    {
        using var ws = new TestWorkspace("guard");
        var real = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        Assert.Throws<InvalidOperationException>(() => ws.Fs.DirectoryExists(real));
        Assert.Throws<InvalidOperationException>(() => ws.Fs.Enumerate(real).ToList());
        Assert.Throws<InvalidOperationException>(() => ws.Fs.OpenRead(Path.Combine(real, "x.jpg")));
        Assert.Throws<InvalidOperationException>(() => ws.Fs.GetAttributes(@"C:\Windows\notepad.exe"));
        // the engine can't even be pointed at real photos from a test
        Assert.Throws<InvalidOperationException>(() => ws.NewEngine().Run(new[] { real }, new ScanControl(), null));
        Assert.Empty(ws.Fs.Opened);
    }
}
