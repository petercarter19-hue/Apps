using System.Diagnostics;
using System.Windows;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>
/// The screen, driven without a real window: the view model runs real scans of test photos, and the screen is drawn
/// off-screen into pictures (at 100% and 125% scaling, light and dark) that are checked for cut-off text.
/// </summary>
[Collection("WPF")]
public class UiTests
{
    private static AppServices Services(TestWorkspace ws, bool withPictures = true) =>
        new(ws.Paths, ws.Fs, ws.Policy, withPictures ? ws.Pictures : "");

    private static async Task<MainViewModel> ScanThroughScreen(AppServices s)
    {
        var vm = WpfHost.Run(() => new MainViewModel(s));
        await WpfHost.Run(() => vm.StartScanAsync());
        return vm;
    }

    /// <summary>Realistic-looking test photos: a phone import with copies, a birthday with chat-app copies, a few one-offs.</summary>
    internal static void MakeSamplePhotos(TestWorkspace ws)
    {
        var cake = TestPhotoFactory.StandIn(11, 3_400_000);
        var beach = TestPhotoFactory.StandIn(12, 5_100_000);
        var dog = TestPhotoFactory.StandIn(13, 2_200_000);
        ws.Photo("2026 Birthday\\IMG_2041.jpg", cake);
        ws.Photo("2026 Birthday\\IMG_2041 (1).jpg", cake);
        ws.Photo("Sent in chat\\IMG-20261012-WA0003.jpg", cake);
        ws.Photo("Phone import\\IMG_3310.heic", beach);
        ws.Photo("Phone import\\IMG_3310 copy.heic", beach);
        ws.Photo("Desktop backup\\IMG_3310.heic", beach);
        ws.Photo("Phone import\\IMG_3377.jpg", dog);
        ws.Photo("Dog park\\Buddy.jpg", dog);
        for (int i = 0; i < 8; i++) ws.Photo($"Phone import\\IMG_{4000 + i}.jpg", TestPhotoFactory.StandIn(100 + i, 1_800_000 + i * 37_000));
    }

    private static void Pictures(MainViewModel vm, string name) => ScreenShots.Take(vm, "step1", name);

    [Fact]
    public async Task A_finished_scan_shows_the_headline_the_groups_and_fits_every_size()
    {
        using var ws = new TestWorkspace("ui-done");
        MakeSamplePhotos(ws);
        using var s = Services(ws);

        var vm = await ScanThroughScreen(s);

        Assert.False(vm.IsScanning);
        Assert.Equal(3, vm.ExactGroups.Count);
        Assert.Empty(vm.SameGroups);
        // cake: 2 extra of 3.4 MB, beach: 2 extra of 5.1 MB, dog: 1 extra of 2.2 MB
        Assert.StartsWith("5 duplicates found. You can free ", vm.Headline);
        Assert.Contains("photos looked at", vm.Subline);
        Assert.Equal("Scan again", vm.ScanButtonText);
        Assert.Equal("", vm.Hint);
        Assert.All(vm.ExactGroups, g => Assert.All(g.Cards, c => { Assert.NotEmpty(c.Name); Assert.NotEmpty(c.FolderText); Assert.NotEmpty(c.SizeAndDims); Assert.NotEmpty(c.TakenText); }));
        Pictures(vm, "finished");
    }

    [Fact]
    public async Task No_duplicates_says_so_plainly()
    {
        using var ws = new TestWorkspace("ui-none");
        for (int i = 0; i < 4; i++) ws.Photo($"p{i}.jpg", TestPhotoFactory.StandIn(i, 3000));
        using var s = Services(ws);

        var vm = await ScanThroughScreen(s);

        Assert.Equal("No duplicates found.", vm.Headline);
        Assert.Empty(vm.ExactGroups);
        Pictures(vm, "none");
    }

    [Fact]
    public void Before_a_scan_the_screen_says_what_to_do_and_with_no_folder_the_scan_button_is_off()
    {
        using var ws = new TestWorkspace("ui-empty");
        using (var s = Services(ws))
        {
            var vm = WpfHost.Run(() => new MainViewModel(s));
            Assert.Contains("Click Scan", vm.Hint);
            Assert.True(vm.ScanCommand.CanExecute(null));
            Assert.Equal("Scan", vm.ScanButtonText);
            Assert.False(vm.PauseResumeCommand.CanExecute(null));
            Assert.False(vm.StopCommand.CanExecute(null));
            Pictures(vm, "before-scan");
        }
        using var ws2 = new TestWorkspace("ui-empty2");
        using var s2 = Services(ws2, withPictures: false);
        var vm2 = WpfHost.Run(() => new MainViewModel(s2));
        Assert.Contains("Add a folder", vm2.Hint);
        Assert.False(vm2.ScanCommand.CanExecute(null));
        Assert.Contains("No folders yet", vm2.FolderSummary);
        Pictures(vm2, "no-folders");
    }

    [Fact]
    public async Task While_scanning_progress_and_time_left_are_shown_and_buttons_make_sense()
    {
        using var ws = new TestWorkspace("ui-progress");
        MakeSamplePhotos(ws);
        using var s = Services(ws);
        s.MaxParallelReads = 2;
        var vm = WpfHost.Run(() => new MainViewModel(s));
        var hold = new ManualResetEventSlim();
        int opens = 0;
        ws.Fs.BeforeOpen = _ => { if (Interlocked.Increment(ref opens) <= 12) Thread.Sleep(450); else hold.Wait(TimeSpan.FromSeconds(30)); };

        var scanning = WpfHost.Run(() => vm.StartScanAsync());
        Assert.True(SpinWait.SpinUntil(() => vm.StatusText.StartsWith("Reading") && vm.ProgressValue >= 3 && System.Text.RegularExpressions.Regex.IsMatch(vm.StatusText, @"(about .* left|less than a minute left)"), 60000), "status: " + vm.StatusText);

        Assert.True(vm.IsScanning);
        Assert.False(vm.CanChangeFolders);                 // can't change folders mid-scan
        Assert.False(vm.ScanCommand.CanExecute(null));     // can't start a second scan
        Assert.True(vm.PauseResumeCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.Equal(Visibility.Visible, vm.ProgressVisibility);
        Assert.Matches(@"Reading \d+ of 16 photos · ", vm.StatusText);
        Assert.False(vm.ProgressIndeterminate);
        Pictures(vm, "scanning");

        hold.Set();
        await scanning;
        Assert.False(vm.IsScanning);
        Assert.Equal(Visibility.Collapsed, vm.ProgressVisibility);
        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pause_shows_Paused_and_the_button_becomes_Resume_then_the_scan_finishes()
    {
        using var ws = new TestWorkspace("ui-pause");
        for (int i = 0; i < 30; i++) ws.Photo($"p{i}.jpg", TestPhotoFactory.StandIn(i % 5, 3000));
        using var s = Services(ws);
        var vm = WpfHost.Run(() => new MainViewModel(s));
        var gate = new ManualResetEventSlim();
        ws.Fs.BeforeOpen = _ => gate.Wait(TimeSpan.FromSeconds(30));
        var scanning = WpfHost.Run(() => vm.StartScanAsync());
        Assert.True(SpinWait.SpinUntil(() => vm.StatusText.StartsWith("Reading"), 30000));

        WpfHost.Run(() => vm.PauseResumeCommand.Execute(null));
        gate.Set();
        Assert.True(vm.IsPaused);
        Assert.Equal("Resume", vm.PauseButtonText);
        Assert.Equal("Paused", vm.StatusText);
        Thread.Sleep(500);
        Assert.True(vm.IsScanning);   // still paused, not finished
        // the page isn't empty while the first scan runs: the card says what is happening
        Assert.Equal("Looking through your photos", vm.HintTitle);
        Assert.Contains("The groups appear here when the scan is done", vm.Hint);
        Assert.Equal(Visibility.Visible, vm.HintVisibility);
        Pictures(vm, "paused");

        WpfHost.Run(() => vm.PauseResumeCommand.Execute(null));
        Assert.Equal("Pause", vm.PauseButtonText);
        await scanning;
        Assert.Equal(5, vm.ExactGroups.Count);
        Assert.StartsWith("25 duplicates found", vm.Headline);
        Assert.Equal("", vm.Hint);                              // and it goes once the groups are there
        Assert.Equal(Visibility.Collapsed, vm.HintVisibility);
    }

    [Fact]
    public async Task Stop_leaves_a_clear_message_and_the_next_start_offers_Continue()
    {
        using var ws = new TestWorkspace("ui-stop");
        for (int i = 0; i < 40; i++) ws.Photo($"p{i}.jpg", TestPhotoFactory.StandIn(i % 8, 3000 + i % 8));
        MainViewModel vm;
        using (var s = Services(ws))
        {
            vm = WpfHost.Run(() => new MainViewModel(s));
            int opens = 0;
            ws.Fs.BeforeOpen = _ => { if (Interlocked.Increment(ref opens) == 10) WpfHost.Run(() => vm.StopCommand.Execute(null)); };
            await WpfHost.Run(() => vm.StartScanAsync());

            Assert.StartsWith("Stopped.", vm.Headline);
            Assert.Equal("Continue", vm.ScanButtonText);
            Assert.Contains("Continue", vm.Banner);
            Assert.False(vm.IsScanning);
            Pictures(vm, "stopped");
        }

        // closing and reopening the app: the unfinished scan is remembered
        ws.Fs.BeforeOpen = null;
        using var s2 = Services(ws);
        var vm2 = WpfHost.Run(() => new MainViewModel(s2));
        Assert.Contains("didn't finish", vm2.Banner);
        Assert.Equal("Continue", vm2.ScanButtonText);

        await WpfHost.Run(() => vm2.StartScanAsync());
        // (these 40 photos are 8 pictures x 5 copies)
        Assert.Equal(8, vm2.ExactGroups.Count);
        Assert.DoesNotContain("didn't finish", vm2.Banner);
        Assert.Equal("Scan again", vm2.ScanButtonText);
    }

    [Fact]
    public async Task Closing_the_window_mid_scan_stops_it_saves_and_does_not_hang()
    {
        using var ws = new TestWorkspace("ui-close");
        for (int i = 0; i < 60; i++) ws.Photo($"p{i}.jpg", TestPhotoFactory.StandIn(i, 3000));
        using var s = Services(ws);
        s.MaxParallelReads = 1;
        var vm = WpfHost.Run(() => new MainViewModel(s));
        int opens = 0;
        ws.Fs.BeforeOpen = _ => { if (Interlocked.Increment(ref opens) <= 30) Thread.Sleep(60); };
        var scanning = WpfHost.Run(() => vm.StartScanAsync());
        Assert.True(SpinWait.SpinUntil(() => vm.StatusText.StartsWith("Reading") && vm.ProgressValue >= 3, 30000));

        await WpfHost.Run(() => vm.StopAndWaitAsync());
        await scanning;

        Assert.False(vm.IsScanning);
        Assert.True(s.Cache.LastScanUnfinished);
        Assert.True(s.Cache.RowCount > 0);
    }

    [Fact]
    public async Task Problems_and_left_out_folders_are_shown_with_a_list()
    {
        using var ws = new TestWorkspace("ui-problems");
        var a = TestPhotoFactory.StandIn(1, 3000);
        ws.Photo("a.jpg", a); ws.Photo("b.jpg", a);
        ws.Photo("empty1.jpg", Array.Empty<byte>()); ws.Photo("empty2.jpg", Array.Empty<byte>());
        ws.Photo("Duplicates to delete\\z.jpg", a);
        TestPhotoFactory.Write(Path.Combine(ws.Other, "x.jpg"), a);
        ws.MakeJunction(Path.Combine(ws.Pictures, "link"), ws.Outside);
        using var s = Services(ws);

        var vm = await ScanThroughScreen(s);

        Assert.Equal("Couldn't read 2 photos. See the list.", vm.ProblemsText);
        Assert.Contains("left out", vm.LeftOutText);
        var win = WpfHost.Run(() => new ProblemsWindow(vm.Result!, s.Log));
        var shown = WpfHost.Run(() => (win.ProblemsItems.Items.Count, win.LeftOutItems.Items.Count, win.ProblemsHeading.Text));
        Assert.Equal(2, shown.Item1);
        Assert.Equal(2, shown.Item2);   // the holding folder and the link
        Assert.Equal("Couldn't read 2 photos", shown.Item3);
        Pictures(vm, "problems");
    }

    [Fact]
    public void A_scan_that_hits_an_unexpected_failure_reports_it_on_screen_and_in_the_log()
    {
        using var ws = new TestWorkspace("ui-error");
        ws.Photo("a.jpg", TestPhotoFactory.StandIn(1, 3000));
        using var s = Services(ws);
        var vm = WpfHost.Run(() => new MainViewModel(s));
        s.Cache.Dispose();   // pull the memory of earlier scans out from under the scan

        WpfHost.Run(() => vm.StartScanAsync()).GetAwaiter().GetResult();

        Assert.Contains("unexpected problem", vm.ErrorText);
        Assert.Contains("log", vm.ErrorText);
        Assert.False(vm.IsScanning);
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.Contains("Scan failed", File.ReadAllText(s.Log.CurrentFile));
        Pictures(vm, "error");
    }

    [Fact]
    public void Damaged_settings_and_cache_are_announced_across_the_top()
    {
        using var ws = new TestWorkspace("ui-damaged");
        File.WriteAllText(ws.Paths.SettingsFile, "not json");
        File.WriteAllText(ws.Paths.CacheFile, "not a database");
        using var s = Services(ws);

        var vm = WpfHost.Run(() => new MainViewModel(s));

        Assert.Contains("settings couldn't be read", vm.Banner);
        Assert.Contains("memory of earlier scans was damaged", vm.Banner);
        Pictures(vm, "damaged");
    }

    [Fact]
    public void Folder_changes_clear_old_results_so_stale_groups_never_show()
    {
        using var ws = new TestWorkspace("ui-folders");
        var a = TestPhotoFactory.StandIn(1, 3000);
        ws.Photo("a.jpg", a); ws.Photo("b.jpg", a);
        using var s = Services(ws);
        var vm = ScanThroughScreen(s).GetAwaiter().GetResult();
        Assert.Single(vm.ExactGroups);

        s.Folders.Remove(ws.Pictures);
        WpfHost.Run(() => vm.RefreshFolders());

        Assert.Empty(vm.ExactGroups);
        Assert.Equal("", vm.Headline);
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.Contains("Add a folder", vm.Hint);
    }

    [Fact]
    public void The_folders_window_lists_folders_with_problems_and_refuses_removing_nothing()
    {
        using var ws = new TestWorkspace("ui-foldwin");
        using var s = Services(ws);
        var gone = Path.Combine(ws.Root, "usb"); Directory.CreateDirectory(gone);
        Assert.Null(s.Folders.TryAdd(gone));
        Directory.Delete(gone);

        var (items, removeMsg, addEnabled) = WpfHost.Run(() =>
        {
            var w = new FoldersWindow(s.Folders);
            var n = w.FolderListBox.Items.Count;
            w.FolderListBox.SelectedItem = null;
            w.RemoveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return (n, w.MessageText.Text, w.AddButton.IsEnabled);
        });

        Assert.Equal(2, items);
        Assert.Contains("Click a folder", removeMsg);
        Assert.True(addEnabled);
    }

    [Fact]
    public void Sizes_and_time_left_read_the_way_people_say_them()
    {
        Assert.Equal("0 bytes", Format.Size(0));
        Assert.Equal("1.0 KB", Format.Size(1024));
        Assert.Equal("3.2 MB", Format.Size((long)(3.2 * 1024 * 1024)));
        Assert.Equal("7.8 GB", Format.Size((long)(7.8 * 1024 * 1024 * 1024)));
        Assert.Equal("120 MB", Format.Size(120L * 1024 * 1024));
        Assert.Equal("less than a minute left", Format.TimeLeft(TimeSpan.FromSeconds(20)));
        Assert.Equal("about 4 min left", Format.TimeLeft(TimeSpan.FromMinutes(4.2)));
        Assert.Equal("about 1 hr 20 min left", Format.TimeLeft(TimeSpan.FromMinutes(80)));
        Assert.Equal("about 2 hr left", Format.TimeLeft(TimeSpan.FromMinutes(120)));
        Assert.Equal("1 duplicate", Format.Plural(1, "duplicate", "duplicates"));
        Assert.Equal("1,284 duplicates", Format.Plural(1284, "duplicate", "duplicates"));
    }

    [Fact]
    public void The_cut_off_checker_really_catches_cut_off_text()
    {
        var bad = WpfHost.Run(() =>
        {
            var tb = new System.Windows.Controls.TextBlock { Text = "A long sentence that cannot possibly fit in a narrow box", TextWrapping = TextWrapping.NoWrap, Width = 60 };
            var host = SelfCheck.Host(tb, 200, 100, 1.25);
            return SelfCheck.FindCutOffText(host);
        });
        Assert.NotEmpty(bad);

        // and text that has room is not flagged
        var fine = WpfHost.Run(() => SelfCheck.FindCutOffText(SelfCheck.Host(new System.Windows.Controls.TextBlock { Text = "Short", Width = 200, TextWrapping = TextWrapping.NoWrap }, 300, 100, 1.25)));
        Assert.Empty(fine);
    }

    [Fact]
    public void The_real_program_starts_draws_itself_and_exits_cleanly_without_a_window()
    {
        var root = Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;
        var exe = Path.Combine(root, "src", "PhotoTwins", "bin", "Debug", "net10.0-windows", "PhotoTwins.exe");
        Assert.True(File.Exists(exe), exe);
        using var ws = new TestWorkspace("ui-exe");
        var png = Path.Combine(TestWorkspace.TestWorkRoot(), "pictures", "step1", "program-selfcheck-125.png");

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--selfcheck"); psi.ArgumentList.Add(Path.Combine(ws.Root, "exe-data")); psi.ArgumentList.Add(png);
        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(60000), "the program did not finish");
        var err = p.StandardError.ReadToEnd();

        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {err}");
        Assert.True(new FileInfo(png).Length > 5000);
    }
}
