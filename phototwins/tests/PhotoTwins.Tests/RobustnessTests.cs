using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>The unhappy routes: nothing to scan, settings that can't be saved, a cache that can't be written, a second copy started.</summary>
[Collection("WPF")]
public class RobustnessTests
{
    private static async Task<MainViewModel> ScanThroughScreen(AppServices s)
    {
        var vm = WpfHost.Run(() => new MainViewModel(s));
        await WpfHost.Run(() => vm.StartScanAsync());
        return vm;
    }

    [Fact]
    public async Task An_empty_folder_says_no_photos_were_found_not_no_duplicates()
    {
        using var ws = new TestWorkspace("rb-empty");
        File.WriteAllText(Path.Combine(ws.Pictures, "notes.txt"), "not a photo");
        using var s = new AppServices(ws.Paths, ws.Fs, ws.Policy, ws.Pictures);

        var vm = await ScanThroughScreen(s);

        Assert.Equal("No photos found in your folders.", vm.Headline);
        Assert.Contains("0 photos looked at", vm.Subline);
    }

    [Fact]
    public async Task When_every_folder_has_gone_missing_the_screen_says_so_and_why()
    {
        using var ws = new TestWorkspace("rb-gone");
        var usb = Path.Combine(ws.Root, "usb-stick"); Directory.CreateDirectory(usb);
        using var s = new AppServices(ws.Paths, ws.Fs, ws.Policy, "");
        Assert.Null(s.Folders.TryAdd(usb));
        Directory.Delete(usb);

        var vm = await ScanThroughScreen(s);

        Assert.Equal("None of your folders could be scanned.", vm.Headline);
        Assert.Contains("left out", vm.LeftOutText);
        var win = WpfHost.Run(() => new ProblemsWindow(vm.Result!, s.Log));
        Assert.Contains("doesn't exist", WpfHost.Run(() => ((LeftOut)win.LeftOutItems.Items[0]!).Reason));
        Assert.NotEqual("No duplicates found.", vm.Headline);
    }

    [Fact]
    public async Task Online_only_photos_are_counted_on_screen_and_the_headline_stays_honest()
    {
        using var ws = new TestWorkspace("rb-online");
        var c = ws.Photo("cloud.jpg", TestPhotoFactory.StandIn(1, 3000));
        ws.Fs.OnlineOnlyFiles.Add(c);
        using var s = new AppServices(ws.Paths, ws.Fs, ws.Policy, ws.Pictures);

        var vm = await ScanThroughScreen(s);

        Assert.Equal("No photos found in your folders.", vm.Headline);
        Assert.Contains("1 online-only photo skipped (never downloaded)", vm.Subline);
        Assert.Empty(ws.Fs.Opened);
    }

    // ---------- settings that can't be saved

    [Fact]
    public void A_folder_list_that_cannot_be_saved_is_not_changed_and_says_why()
    {
        using var ws = new TestWorkspace("rb-save");
        Directory.CreateDirectory(ws.Paths.SettingsFile);   // a folder sitting where the settings file should go: saving must fail
        var store = new SettingsStore(ws.Paths, ws.Log);
        var list = new FolderList(store.Load(), store, ws.Fs, ws.Policy);

        var why = list.TryAdd(ws.Pictures);

        Assert.NotNull(why);
        Assert.Contains("couldn't save", why);
        Assert.Empty(list.Folders);
        Assert.Contains("Settings could not be saved", File.ReadAllText(ws.Log.CurrentFile));
    }

    [Fact]
    public void Settings_that_cannot_be_saved_at_start_up_are_announced_and_the_app_still_starts()
    {
        using var ws = new TestWorkspace("rb-save-start");
        Directory.CreateDirectory(ws.Paths.SettingsFile);

        using var s = new AppServices(ws.Paths, ws.Fs, ws.Policy, ws.Pictures);

        Assert.Contains(s.StartupNotes(), n => n.Contains("couldn't save your settings"));
        var vm = WpfHost.Run(() => new MainViewModel(s));
        Assert.Contains("couldn't save your settings", vm.Banner);
        Assert.Single(s.Folders.Folders);   // still usable this time round
    }

    // ---------- a cache that can't be written

    private sealed class FullDiskCache : PhotoCache
    {
        public FullDiskCache(string path, AppLog log) : base(path, log) { }
        public override void Upsert(IReadOnlyCollection<CachedPhoto> rows, long scanId) => throw new IOException("There is not enough space on the disk.");
    }

    [Fact]
    public void When_the_memory_of_the_scan_cannot_be_saved_the_scan_still_finishes_and_says_so()
    {
        using var ws = new TestWorkspace("rb-fulldisk");
        var a = TestPhotoFactory.StandIn(1, 3000);
        ws.Photo("a.jpg", a); ws.Photo("b.jpg", a); ws.Photo("c.jpg", TestPhotoFactory.StandIn(2, 3000));
        using var cache = new FullDiskCache(ws.Paths.CacheFile, ws.Log);

        var r = new ScanEngine(ws.Fs, cache, ws.Log, ws.Policy).Run(new[] { ws.Pictures }, new ScanControl(), null);

        Assert.True(r.Completed);
        Assert.Empty(r.Problems);                       // the photos were fine
        Assert.Single(r.ExactGroups);
        Assert.NotNull(r.CacheWarning);
        Assert.Contains("not enough space", r.CacheWarning);
        Assert.Contains("Couldn't save to the cache", File.ReadAllText(ws.Log.CurrentFile));

        using var ws2 = new TestWorkspace("rb-fulldisk-ui");
        using var s = new AppServices(ws2.Paths, ws2.Fs, ws2.Policy, ws2.Pictures);
        var vm = WpfHost.Run(() => new MainViewModel(s));
        vm.ApplyResult(r);
        Assert.Contains("couldn't save its memory", vm.Banner);
    }

    // ---------- many groups: only what's on screen is built

    [Fact]
    public void A_huge_result_scrolls_smoothly_because_only_the_visible_groups_are_drawn()
    {
        using var ws = new TestWorkspace("rb-huge");
        using var s = new AppServices(ws.Paths, ws.Fs, ws.Policy, ws.Pictures);
        var groups = new List<ExactGroup>();
        for (int i = 0; i < 5000; i++)
        {
            var sha = new byte[32]; BitConverter.GetBytes(i).CopyTo(sha, 0);
            groups.Add(new ExactGroup(new[]
            {
                new PhotoFile($@"C:\Pics\IMG_{i}.jpg", 3_000_000, DateTime.UtcNow.Ticks, sha, new FileIdentity(1, (ulong)(2 * i))),
                new PhotoFile($@"C:\Pics\Backup\IMG_{i}.jpg", 3_000_000, DateTime.UtcNow.Ticks, sha, new FileIdentity(1, (ulong)(2 * i + 1))),
            }));
        }
        var vm = WpfHost.Run(() => new MainViewModel(s));
        vm.ApplyResult(new ScanResult { Completed = true, Roots = new[] { ws.Pictures }, PhotosFound = 10000, ExactGroups = groups });

        var realized = WpfHost.Run(() =>
        {
            var view = new MainView { DataContext = vm };
            SelfCheck.Host(view, 1180, 780, 1.25);
            return CountRealizedGroupCards(view.ExactList);
        });

        Assert.Equal("5,000 duplicates found. You can free 14.0 GB.", vm.Headline);
        Assert.InRange(realized, 1, 40);   // not 5,000
    }

    private static int CountRealizedGroupCards(DependencyObject root)
    {
        int count = 0;
        void Walk(DependencyObject d)
        {
            if (d is TextBlock { Text: { } t } && t.Contains("identical copies")) count++;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return count;
    }

    // ---------- closing the window

    [Fact]
    public async Task Closing_the_real_window_during_a_scan_stops_saves_then_closes_and_the_next_start_offers_Continue()
    {
        using var ws = new TestWorkspace("rb-closewin");
        for (int i = 0; i < 60; i++) ws.Photo($"p{i}.jpg", TestPhotoFactory.StandIn(i, 3000));
        using var s = new AppServices(ws.Paths, ws.Fs, ws.Policy, ws.Pictures) { MaxParallelReads = 1 };
        int opens = 0;
        ws.Fs.BeforeOpen = _ => { if (Interlocked.Increment(ref opens) <= 40) Thread.Sleep(60); };
        var closed = new TaskCompletionSource();
        var (win, vm) = WpfHost.Run(() =>
        {
            var v = new MainViewModel(s);
            var w = new MainWindow(v);
            w.Closed += (_, _) => closed.TrySetResult();
            _ = v.StartScanAsync();
            return (w, v);
        });
        Assert.True(SpinWait.SpinUntil(() => vm.ProgressValue >= 3, 30000));

        WpfHost.Run(() => win.Close());   // the user clicks the window's close button mid-scan

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(vm.IsScanning);
        Assert.True(s.Cache.LastScanUnfinished);
        Assert.True(s.Cache.RowCount >= 3);
        var next = WpfHost.Run(() => new MainViewModel(s));
        Assert.Equal("Continue", next.ScanButtonText);
        Assert.Contains("didn't finish", next.Banner);
    }

    // ---------- a second copy

    [Fact]
    public void Starting_a_second_copy_does_not_open_a_second_one_it_asks_the_first_to_come_forward()
    {
        var name = "PhotoTwinsTest" + Guid.NewGuid().ToString("N");
        int signalled = 0;
        using var first = new SingleInstance(name, () => Interlocked.Increment(ref signalled));
        Assert.True(first.IsFirst);

        using var second = new SingleInstance(name, () => { });

        Assert.False(second.IsFirst);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref signalled) == 1, 5000), "the first copy was never told");
    }
}
