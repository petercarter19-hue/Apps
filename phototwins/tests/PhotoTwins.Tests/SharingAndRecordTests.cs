using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Settings;
using PhotoTwins.Core.Sharing;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Which folders count as shared or as cloud, and what happens when the removal record itself can't be written.</summary>
public class SharingAndRecordTests
{
    // ------------------------------------------------------------ shared folders

    [Fact]
    public void A_photo_is_shared_if_any_folder_above_it_is_and_each_folder_is_only_asked_about_once()
    {
        var asked = new List<string>();
        var d = new SharedFolderDetector(new Func<string, string?>[]
        {
            f => { lock (asked) asked.Add(f); return f.EndsWith(@"\Family", StringComparison.OrdinalIgnoreCase) ? "test: shared" : null; },
        }, () => Array.Empty<string>());

        Assert.Equal("test: shared", d.WhyShared(@"C:\Pics\Family\2026\Trip\a.jpg"));
        Assert.Equal("test: shared", d.WhyShared(@"C:\Pics\Family\2026\Trip\b.jpg"));
        Assert.Null(d.WhyShared(@"C:\Pics\Mine\a.jpg"));
        Assert.Equal(1, asked.Count(f => f == @"C:\Pics\Family\2026\Trip"));   // remembered after the first time
        d.ForgetEverything();
        Assert.Equal("test: shared", d.WhyShared(@"C:\Pics\Family\x.jpg"));
    }

    [Fact]
    public void Folders_Pete_marks_as_shared_count_and_a_failing_probe_is_not_fatal()
    {
        var marked = new List<string> { @"C:\Pics\Cousins" };
        var d = new SharedFolderDetector(new Func<string, string?>[] { _ => throw new InvalidOperationException("the probe broke") }, () => marked);

        Assert.Contains("You marked", d.WhyShared(@"C:\Pics\Cousins\Beach\a.jpg"));
        Assert.Null(d.WhyShared(@"C:\Pics\Mine\a.jpg"));                       // a probe that throws just says "not shared"
        Assert.Null(d.WhyShared(@"C:\Pics\CousinsOther\a.jpg"));               // a folder whose name merely starts the same is not inside
    }

    [Fact]
    public void The_real_detector_knows_the_Public_folder_and_network_locations_but_not_an_ordinary_folder()
    {
        var policy = ScanPolicy.ForThisPc();
        var real = SharedFolderDetector.ForThisPc(CloudFolderDetector.ForThisPc(policy), () => Array.Empty<string>());

        Assert.Contains("Public folder", real.WhyShared(@"C:\Users\Public\Pictures\a.jpg"));
        Assert.Contains("network", real.WhyShared(@"\\server\share\Photos\a.jpg"));
        Assert.Null(real.WhyShared(Path.Combine(TestWorkspace.TestWorkRoot(), "x", "a.jpg")));
        Assert.Contains("Google Drive", real.WhyShared(@"G:\Shared drives\Team\a.jpg"));
        Assert.Contains("Google Drive", real.WhyShared(@"G:\.shortcut-targets-by-id\abc\Family\a.jpg"));
    }

    [Fact]
    public void Shared_folders_Pete_adds_are_checked_remembered_and_never_lost_on_a_failed_save()
    {
        using var ws = new TestWorkspace("sh-list");
        var store = new SettingsStore(ws.Paths, ws.Log);
        var list = new FolderList(store.Load(), store, ws.Fs, ws.Policy);
        var family = Path.Combine(ws.Pictures, "Family"); Directory.CreateDirectory(family);

        Assert.Null(list.TryAddShared(family));
        Assert.Contains("already marked", list.TryAddShared(family));
        Directory.CreateDirectory(Path.Combine(family, "Sub"));
        Assert.Contains("already marked", list.TryAddShared(Path.Combine(family, "Sub")));   // inside a shared folder: nothing to add
        Assert.Contains("doesn't exist", list.TryAddShared(Path.Combine(ws.Root, "nope")));
        Assert.Contains("full folder", list.TryAddShared("Family"));
        Assert.Equal(new[] { ScanPolicy.Normalize(family) }, new FolderList(store.Load(), store, ws.Fs, ws.Policy).SharedFolders);

        Assert.Null(list.RemoveShared(family));
        Assert.Empty(store.Load().SharedFolders);

        // a save that fails leaves the list as it was
        File.Delete(ws.Paths.SettingsFile); Directory.CreateDirectory(ws.Paths.SettingsFile);   // a folder where the settings file should be
        var why = list.TryAddShared(family);
        Assert.Contains("couldn't save", why);
        Assert.Empty(list.SharedFolders);
    }

    [Fact]
    public void The_folder_list_says_which_picked_folders_are_shared()
    {
        using var ws = new TestWorkspace("sh-describe");
        var store = new SettingsStore(ws.Paths, ws.Log);
        var settings = store.Load();
        var list = new FolderList(settings, store, ws.Fs, ws.Policy);
        var family = Path.Combine(ws.Pictures, "Family"); Directory.CreateDirectory(family);
        list.SharedDetector = new SharedFolderDetector(Array.Empty<Func<string, string?>>(), () => settings.SharedFolders);
        Assert.Null(list.TryAdd(family));
        Assert.Null(list.TryAddShared(family));

        var d = Assert.Single(list.Describe());

        Assert.Contains("You marked", d.SharedReason);
    }

    // ------------------------------------------------------------ cloud folders

    [Fact]
    public void Cloud_folders_are_recognised_by_the_nearest_root_and_named_for_the_message()
    {
        var d = new CloudFolderDetector(new[] { new CloudRoot(@"C:\Users\p\OneDrive", "OneDrive"), new CloudRoot(@"C:\Users\p\iCloudPhotos\Photos", "iCloud Photos") });

        Assert.Equal("OneDrive", d.ServiceOf(@"C:\Users\p\OneDrive\Media\a.jpg"));
        Assert.Equal("OneDrive", d.ServiceOf(@"c:\users\p\onedrive\a.jpg"));
        Assert.Equal("iCloud Photos", d.ServiceOf(@"C:\Users\p\iCloudPhotos\Photos\a.jpg"));
        Assert.Null(d.ServiceOf(@"C:\Users\p\OneDriveBackup\a.jpg"));          // only the same name at the start
        Assert.Null(d.ServiceOf(@"C:\Users\p\Pictures\a.jpg"));
        Assert.Equal("Google Drive", new CloudFolderDetector(Array.Empty<CloudRoot>(), p => p.StartsWith("G:")).ServiceOf(@"G:\My Drive\a.jpg"));
    }

    [Fact]
    public void The_real_cloud_roots_of_this_PC_include_the_OneDrive_folder_Windows_reports()
    {
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrEmpty(oneDrive)) return;   // this PC has no OneDrive
        var d = CloudFolderDetector.ForThisPc(ScanPolicy.ForThisPc());
        Assert.Equal("OneDrive", d.ServiceOf(Path.Combine(oneDrive, "Pictures", "a.jpg")));
        Assert.Null(d.ServiceOf(Path.Combine(TestWorkspace.TestWorkRoot(), "a.jpg")));
    }

    // ------------------------------------------------------------ the record can't be written

    private sealed class FailingJournal : UndoJournal
    {
        public int FailFromAppend { get; init; }
        private int _n;
        public FailingJournal(string path, PhotoTwins.Core.Logging.AppLog log) : base(path, log) { }
        public override void Append(JournalEntry e)
        {
            if (++_n >= FailFromAppend) throw new IOException("the disk is full (test)");
            base.Append(e);
        }
    }

    [Fact]
    public void If_the_record_cannot_be_written_before_a_removal_the_photo_is_not_touched_at_all()
    {
        using var ws = new TestWorkspace("rec-before");
        var bytes = TestPhotoFactory.StandIn(7, 5000);
        var a = ws.Photo("a.jpg", bytes); var b = ws.Photo("b.jpg", bytes);
        var journal = new FailingJournal(Path.Combine(ws.Paths.DataDir, "j.jsonl"), ws.Log) { FailFromAppend = 1 };
        var m = new FileMutations(ws.Fs, ws.Log, journal, new NoBinRules());
        var gate = new SafetyGate(ws.Fs, null, null);
        var service = new RemovalService(gate, m, ws.Log);
        var scan = ws.NewEngine().Run(new[] { ws.Pictures }, new ScanControl(), null);
        var g = scan.Decided.Single();
        var victim = g.Others.Single();
        var before = FileSnapshot.Of(victim.Path);

        var r = service.Execute(RemovalPlan.ForGroup(g, new[] { ws.Pictures }), new Confirmations(), null, CancellationToken.None);

        Assert.Empty(r.Removed);
        var f = Assert.Single(r.Failed);
        Assert.Contains("disk is full", f.Why);
        Assert.Equal(before, FileSnapshot.Of(victim.Path));    // nothing moved
        Assert.False(Directory.Exists(Path.Combine(ws.Pictures, "Duplicates to delete")));
        _ = a; _ = b;
    }

    [Fact]
    public void If_the_record_cannot_be_finished_after_a_move_the_photo_still_counts_and_the_next_start_finishes_the_record()
    {
        using var ws = new TestWorkspace("rec-after");
        var bytes = TestPhotoFactory.StandIn(8, 5000);
        ws.Photo("a.jpg", bytes); ws.Photo("b.jpg", bytes);
        var jpath = Path.Combine(ws.Paths.DataDir, "j.jsonl");
        // the first line ("starting" + where it will go) is written; later lines fail
        var journal = new FailingJournal(jpath, ws.Log) { FailFromAppend = 3 };
        var m = new FileMutations(ws.Fs, ws.Log, journal, new NoBinRules());
        var service = new RemovalService(new SafetyGate(ws.Fs, null, null), m, ws.Log);
        var g = ws.NewEngine().Run(new[] { ws.Pictures }, new ScanControl(), null).Decided.Single();
        var victim = g.Others.Single();

        var r = service.Execute(RemovalPlan.ForGroup(g, new[] { ws.Pictures }), new Confirmations(), null, CancellationToken.None);

        var moved = Assert.Single(r.Removed);                  // it IS removed, and the screen says so
        Assert.False(File.Exists(victim.Path));
        Assert.True(File.Exists(moved.Destination!));
        Assert.Contains("couldn't be finished", File.ReadAllText(ws.Log.CurrentFile));

        // next start-up: the record on disk says "starting"; recovery sees the photo in the holding folder and completes it
        var again = new UndoJournal(jpath, ws.Log);
        Assert.Equal(EntryStatus.Starting, again.Entries().Last().Status);
        var notes = new FileMutations(ws.Fs, ws.Log, again, new NoBinRules()).Reconcile();
        Assert.Single(notes);
        Assert.Equal(EntryStatus.Done, again.Entries().Last().Status);
        Assert.True(new FileMutations(ws.Fs, ws.Log, again, new NoBinRules()).PutBack(again.Entries().Last()).Ok);
        Assert.True(File.Exists(victim.Path));
    }

    // ------------------------------------------------------------ the real Recycle Bin through the whole screen

    [Fact]
    public async Task Through_the_screen_with_the_real_Recycle_Bin_a_removed_photo_is_in_the_bin_and_Undo_returns_it()
    {
        using var e = new Step3Env("rb-ui", realBin: true);
        e.Identical(140, "a.jpg", "b.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var victim = g.Model.Others.Single();
        var before = FileSnapshot.Of(victim.Path);

        await e.RemoveAsync(g);

        Assert.False(File.Exists(victim.Path));
        Assert.StartsWith("Removed 1 photo", e.Vm.ResultText);
        Assert.Contains("Recycle Bin", e.Vm.ResultText);
        Assert.DoesNotContain("Duplicates to delete", e.Vm.ResultText);
        Assert.NotEmpty(ShellRecycleProbe.FindInBin(victim.Path));
        var undo = WpfHost.Run(() => new UndoViewModel(e.S));
        var row = undo.Actions.Single().Rows.Single();
        Assert.Equal("In the Recycle Bin", row.WhereText);

        await WpfHost.Run(() => undo.PutBackAsync(row.Entry));

        Assert.Equal(before.Sha, FileSnapshot.Of(victim.Path).Sha);
        Assert.Empty(ShellRecycleProbe.FindInBin(victim.Path));
    }
}
