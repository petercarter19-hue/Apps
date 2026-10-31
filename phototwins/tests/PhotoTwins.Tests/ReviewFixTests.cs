using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Core.Sharing;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>
/// The seven findings of the independent delete-safety review of the Remove/Undo step, one group of tests each. Real files and the real
/// Recycle Bin on X:, made-up photos only.
/// </summary>
[Collection("WPF")]
public class ReviewFixTests
{
    /// <summary>A drive whose Recycle Bin has a made-up size and fullness (and which is otherwise fine for recycling).</summary>
    private sealed class SizedBin(long? capacity, long? used) : VolumeRules
    {
        public override string? WhyNotRecyclable(string path, long fileSize) => null;
        public override long? BinCapacityBytes(string root) => capacity;
        public override long? BinUsedBytes(string root) => used;
    }

    private static long SizeOf(string path) => new FileInfo(path).Length;

    private static void Touch(string path)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(new byte[] { 1, 2, 3, 4 });
    }

    // ============================================================ 1. The Recycle Bin can overflow

    [Fact]
    public void Finding1_Bin_overflow_the_whole_action_is_added_up_and_what_does_not_fit_goes_to_the_holding_folder()
    {
        // four identical photos = one keeper + three to remove; the made-up bin has room for exactly ONE of them
        using var probe = new RemovalHarness("f1-size");
        var one = probe.Identical(31, "a.jpg");
        var size = SizeOf(one[0]);
        var rules = new SizedBin((long)(size * 1.8), 0);   // 90% of that is 1.62 photos

        using var h = new RemovalHarness("f1-sum", rules);
        h.Identical(31, "a.jpg", "b.jpg", "c.jpg", "d.jpg");
        var g = Assert.Single(h.Scan().Decided);

        var result = h.Run(h.PlanFor(g));

        Assert.Null(result.Refusal);
        Assert.Equal(3, result.Removed.Count);
        Assert.Equal(1, result.RecycledCount);                 // only what fits goes in the bin
        Assert.Equal(2, result.MovedToFolderCount);            // the rest wait in "Duplicates to delete": nothing is lost
        foreach (var e in result.Removed.Where(x => x.Method == RemoveMethod.Folder))
        {
            Assert.True(File.Exists(e.Destination!));
            Assert.Contains("nearly full", e.Reason);          // and the reason is kept to tell Pete
        }
        Assert.True(File.Exists(g.Keeper.Path));               // the keeper is never touched
    }

    [Fact]
    public void Finding1_Bin_overflow_a_bin_that_is_already_nearly_full_gets_nothing_from_this_action()
    {
        using var probe = new RemovalHarness("f1-size2");
        var size = SizeOf(probe.Identical(32, "a.jpg")[0]);
        var rules = new SizedBin(size * 20, (long)(size * 20 * 0.89));   // 89% full already: not even one more fits under the 90% line

        using var h = new RemovalHarness("f1-full", rules);
        h.Identical(32, "a.jpg", "b.jpg", "c.jpg");
        var g = Assert.Single(h.Scan().Decided);

        var result = h.Run(h.PlanFor(g));

        Assert.Equal(0, result.RecycledCount);
        Assert.Equal(2, result.MovedToFolderCount);
        Assert.All(result.Removed, e => Assert.Contains("nearly full", e.Reason));
    }

    [Fact]
    public void Finding1_Bin_overflow_if_Windows_cannot_say_how_full_the_bin_is_the_safe_route_is_used()
    {
        var ledger = new BinLedger(new SizedBin(1L << 40, null));
        Assert.Contains("couldn't say how full", ledger.WhyNoRoom(@"X:\a.jpg", 1000));
        ledger = new BinLedger(new SizedBin(null, 0));
        Assert.Contains("couldn't say how full", ledger.WhyNoRoom(@"X:\a.jpg", 1000));
        ledger = new BinLedger(new SizedBin(1L << 40, 0));
        Assert.Null(ledger.WhyNoRoom(@"X:\a.jpg", 1000));
    }

    [Fact]
    public void Finding1_Bin_overflow_the_real_Windows_bin_size_can_be_read_on_this_PC()
    {
        var v = new VolumeRules();
        var root = Path.GetPathRoot(TestWorkspace.TestWorkRoot())!;
        Assert.NotNull(v.BinCapacityBytes(root));
        var used = v.BinUsedBytes(root);
        Assert.NotNull(used);                                  // (this is what the ledger starts from)
        Assert.True(used >= 0);
    }

    [Fact]
    public async Task Finding1_Bin_overflow_the_result_message_says_which_photos_waited_and_why()
    {
        using var probe = new RemovalHarness("f1-size3");
        var size = SizeOf(probe.Identical(33, "a.jpg")[0]);
        using var e = new Step3Env("f1-ui", new SizedBin((long)(size * 1.8), 0));
        e.Identical(33, "a.jpg", "b.jpg", "c.jpg", "d.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();

        await e.RemoveAsync(g);

        Assert.Contains("Recycle Bin", e.Vm.ResultText);
        Assert.Contains("Duplicates to delete", e.Vm.ResultText);
        Assert.Contains("nearly full", e.Vm.ResultText);
        ScreenShots.Take(e.Vm, "step4", "result-bin-nearly-full", height: 900);   // the wording on screen: nothing cut off, readable in light and dark at 100% and 125%
    }

    // ============================================================ 2. Checks must be made again just before each photo moves

    /// <summary>Four identical photos; <paramref name="beforeFirst"/> runs after the safety check but before the first photo moves.</summary>
    private static (RemovalHarness h, DecidedGroup g, RemovalResult r, string keeper, string[] others) RunWithMischief(
        string name, Action<RemovalHarness, string, string[]> beforeFirst)
    {
        var h = new RemovalHarness(name);
        h.Identical(40, "a.jpg", "b.jpg", "c.jpg", "d.jpg");
        var g = Assert.Single(h.Scan().Decided);
        var keeper = g.Keeper.Path;
        var others = g.Others.Select(o => o.Path).ToArray();
        bool done = false;
        var progress = new SyncProgress<RemovalProgress>(p => { if (!done && p.Current != "") { done = true; beforeFirst(h, keeper, others); } });
        var r = h.Service.Execute(h.PlanFor(g), new Confirmations(), progress, CancellationToken.None);
        return (h, g, r, keeper, others);
    }

    [Fact]
    public void Finding2_Recheck_the_keeper_is_deleted_during_the_removal_so_no_further_copy_is_removed()
    {
        var (h, _, r, keeper, others) = RunWithMischief("f2-keeper-gone", (_, k, _) => File.Delete(k));
        using var _h = h;

        Assert.Empty(r.Removed);                               // the very first copy is already left alone
        Assert.Equal(3, r.Dropped.Count);
        Assert.All(r.Dropped, d => Assert.Contains("photo to keep", d.Why));
        Assert.All(others, o => Assert.True(File.Exists(o))); // every copy is still where it was
    }

    [Fact]
    public void Finding2_Recheck_the_keeper_changes_during_the_removal_so_the_rest_are_left_alone()
    {
        var (h, _, r, keeper, others) = RunWithMischief("f2-keeper-changed", (_, k, _) => Touch(k));
        using var _h = h;

        Assert.Empty(r.Removed);
        Assert.All(r.Dropped, d => Assert.Contains("changed", d.Why));
        Assert.All(others, o => Assert.True(File.Exists(o)));
    }

    [Fact]
    public void Finding2_Recheck_the_keeper_becomes_online_only_during_the_removal_and_is_never_opened_again()
    {
        var (h, _, r, keeper, others) = RunWithMischief("f2-keeper-cloud", (hh, k, _) => hh.Ws.Fs.BecameOnlineOnly.Add(k));
        using var _h = h;

        Assert.Empty(r.Removed);
        Assert.All(r.Dropped, d => Assert.Contains("online-only", d.Why));
        Assert.Empty(h.Ws.Fs.ForbiddenOpens);                  // asking is done from the file's flags: nothing was downloaded
        Assert.All(others, o => Assert.True(File.Exists(o)));
    }

    [Fact]
    public void Finding2_Recheck_a_copy_that_changed_after_the_check_is_left_alone_but_the_others_still_go()
    {
        var (h, _, r, keeper, others) = RunWithMischief("f2-target-changed", (_, _, o) => Touch(o[^1]));
        using var _h = h;

        Assert.Equal(2, r.Removed.Count);
        var left = Assert.Single(r.Dropped);
        Assert.Equal(others[^1], left.File.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("changed", left.Why);
        Assert.True(File.Exists(others[^1]));                  // the changed copy is untouched
        Assert.True(File.Exists(keeper));
    }

    [Fact]
    public void Finding2_Recheck_a_copy_that_became_online_only_is_left_alone_and_never_opened()
    {
        var (h, _, r, keeper, others) = RunWithMischief("f2-target-cloud", (hh, _, o) => hh.Ws.Fs.BecameOnlineOnly.Add(o[0]));
        using var _h = h;

        Assert.Equal(2, r.Removed.Count);
        var left = Assert.Single(r.Dropped);
        Assert.Equal(others[0], left.File.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("online-only", left.Why);
        Assert.Empty(h.Ws.Fs.ForbiddenOpens);
    }

    [Fact]
    public void Finding2_Recheck_a_copy_replaced_by_a_different_file_after_the_check_is_left_alone()
    {
        var (h, _, r, keeper, others) = RunWithMischief("f2-target-replaced", (hh, _, o) =>
        {
            var other = hh.Ws.Photo("zz-other.jpg", TestPhotoFactory.StandIn(41, 5000));
            File.Delete(o[0]);
            hh.Ws.MakeHardLink(o[0], other);   // the same name now leads to a different file
        });
        using var _h = h;

        Assert.Contains(r.Dropped, d => string.Equals(d.File.Path, others[0], StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(others[0]));
        Assert.True(File.Exists(keeper));
    }

    [Fact]
    public void Finding2_Recheck_nothing_changes_so_a_normal_removal_is_unaffected()
    {
        var (h, _, r, keeper, others) = RunWithMischief("f2-calm", (_, _, _) => { });
        using var _h = h;

        Assert.Equal(3, r.Removed.Count);
        Assert.Empty(r.Dropped);
        Assert.True(File.Exists(keeper));
    }

    // ============================================================ 3. Undo must not bring back the wrong Recycle Bin item

    [Fact]
    public void Finding3_Wrong_bin_item_a_bin_file_with_the_same_name_but_different_contents_is_never_restored()
    {
        using var h = new RemovalHarness("f3-mismatch");
        h.Identical(50, "a.jpg", "b.jpg");
        var g = Assert.Single(h.Scan().Decided);
        var entry = Assert.Single(h.Run(h.PlanFor(g)).Removed);
        Assert.Equal(RemoveMethod.Recycle, entry.Method);

        // the record claims different contents (so the only thing in the bin with this name is "a different file")
        var wrong = entry with { Sha256 = new string('0', 64), Destination = null };
        var back = h.Mutations.PutBack(wrong);

        Assert.False(back.Ok);
        Assert.Contains("different file", back.Message);
        Assert.False(File.Exists(entry.OriginalPath));         // nothing was put there
        Assert.NotEmpty(ShellRecycleProbe.FindInBin(entry.OriginalPath));   // and the item is still in the bin, untouched

        var right = h.Mutations.PutBack(entry);                // the real record still works
        Assert.True(right.Ok, right.Message);
    }

    [Fact]
    public void Finding3_Wrong_bin_item_two_different_photos_that_were_both_recycled_from_the_same_path_each_come_back_as_themselves()
    {
        using var h = new RemovalHarness("f3-two");
        var pics = h.Ws.Pictures;
        var first = TestPhotoFactory.StandIn(51, 5000);
        var second = TestPhotoFactory.StandIn(52, 5000);

        // round 1: Sub\b.jpg (contents 1) is removed because a.jpg is picked as the one to keep
        h.Ws.Photo("a.jpg", first); h.Ws.Photo(@"Sub\b.jpg", first);
        var g1 = Assert.Single(h.Scan().Decided);
        h.Picks[g1.Key] = Path.Combine(pics, "a.jpg");
        g1 = Assert.Single(h.Scan().Decided);
        var e1 = Assert.Single(h.Run(h.PlanFor(g1)).Removed);
        Assert.Equal(RemoveMethod.Recycle, e1.Method);
        File.Delete(Path.Combine(pics, "a.jpg"));              // tidy the first round away

        // round 2: a different photo now sits at the very same path and is removed the same way
        h.Ws.Photo("a2.jpg", second); h.Ws.Photo(@"Sub\b.jpg", second);
        var g2 = Assert.Single(h.Scan().Decided);
        h.Picks[g2.Key] = Path.Combine(pics, "a2.jpg");
        g2 = Assert.Single(h.Scan().Decided);
        var e2 = Assert.Single(h.Run(h.PlanFor(g2)).Removed);
        Assert.Equal(e1.OriginalPath, e2.OriginalPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, ShellRecycleProbe.FindInBin(e1.OriginalPath).Count);   // two items in the bin, same original path

        // with the "where it is" part of both records forgotten, only the CONTENTS can tell them apart
        var back2 = h.Mutations.PutBack(e2 with { Destination = null });
        Assert.True(back2.Ok, back2.Message);
        Assert.Equal(second, File.ReadAllBytes(e2.OriginalPath));
        File.Delete(e2.OriginalPath);
        var back1 = h.Mutations.PutBack(e1 with { Destination = null });
        Assert.True(back1.Ok, back1.Message);
        Assert.Equal(first, File.ReadAllBytes(e1.OriginalPath));
    }

    [Fact]
    public void Finding3_Wrong_bin_item_recovery_after_an_interruption_also_needs_the_contents_to_match()
    {
        using var h = new RemovalHarness("f3-recover");
        h.Identical(53, "a.jpg", "b.jpg");
        var g = Assert.Single(h.Scan().Decided);
        var done = Assert.Single(h.Run(h.PlanFor(g)).Removed);
        // as if the "done" line was never written, and the record's idea of the contents is wrong: it must NOT be called finished
        h.Journal.Append(done with { Status = EntryStatus.Starting, Destination = null, Sha256 = new string('0', 64) });

        h.Mutations.Reconcile();

        Assert.NotEqual(EntryStatus.Done, h.Journal.Entries().Single().Status);
        Assert.True(h.Mutations.PutBack(done).Ok);             // (and the real one is still put back from the bin)
    }

    // ============================================================ 4. "Put back all" reads the whole bin once, not once per photo

    [Fact]
    public void Finding4_One_read_putting_back_five_photos_reads_the_whole_Recycle_Bin_once()
    {
        using var h = new RemovalHarness("f4-once");
        h.Identical(60, "a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.jpg", "f.jpg");
        var g = Assert.Single(h.Scan().Decided);
        var removed = h.Run(h.PlanFor(g)).Removed;
        Assert.Equal(5, removed.Count);
        Assert.All(removed, e => Assert.Equal(RemoveMethod.Recycle, e.Method));

        var before = ShellRecycle.BinReads;
        var snapshot = h.Mutations.LoadBinSnapshot();
        foreach (var e in removed) Assert.True(h.Mutations.PutBack(e, snapshot).Ok);

        Assert.Equal(before + 1, ShellRecycle.BinReads);       // once, however many photos
        Assert.All(removed, e => Assert.True(File.Exists(e.OriginalPath)));
    }

    [Fact]
    public void Finding4_One_read_recovery_of_several_interrupted_entries_reads_the_bin_once_and_not_at_all_if_not_needed()
    {
        using var h = new RemovalHarness("f4-reconcile");
        h.Identical(61, "a.jpg", "b.jpg", "c.jpg", "d.jpg");
        var g = Assert.Single(h.Scan().Decided);
        var removed = h.Run(h.PlanFor(g)).Removed;
        foreach (var e in removed) h.Journal.Append(e with { Status = EntryStatus.Starting, Destination = null });

        var before = ShellRecycle.BinReads;
        h.Mutations.Reconcile();
        Assert.Equal(before + 1, ShellRecycle.BinReads);       // three interrupted entries, one look inside the bin
        Assert.All(h.Journal.Entries(), e => Assert.Equal(EntryStatus.Done, e.Status));

        before = ShellRecycle.BinReads;
        h.Mutations.Reconcile();                               // nothing is waiting: the bin isn't read at all
        Assert.Equal(before, ShellRecycle.BinReads);
    }

    [Fact]
    public async Task Finding4_One_read_the_Undo_screen_puts_back_all_through_one_bin_reading_and_restores_every_photo()
    {
        using var e = new Step3Env("f4-ui", realBin: true);
        e.Identical(62, "a.jpg", "b.jpg", "c.jpg", "d.jpg");
        await e.ScanAsync();
        var g = e.Vm.ExactGroups.Single();
        var victims = g.Model.Others.Select(o => o.Path).ToArray();
        await e.RemoveAsync(g);
        Assert.All(victims, v => Assert.False(File.Exists(v)));

        var undo = WpfHost.Run(() => new UndoViewModel(e.S));
        var action = undo.Actions.Single();
        await WpfHost.Run(() => undo.PutBackAllAsync(action.AllRows));

        Assert.All(victims, v => Assert.True(File.Exists(v)));
        var src = File.ReadAllText(Path.Combine(AppRoot(), "src", "PhotoTwins", "UndoViewModel.cs"));
        Assert.Equal(1, Regex.Matches(src, @"LoadBinSnapshot\(").Count);   // the screen reads the bin in exactly one place per action
    }

    private static string AppRoot() => Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;

    // ============================================================ 5. A picked folder that is itself a shortcut link

    [Fact]
    public void Finding5_Folder_link_a_picked_folder_that_really_leads_into_another_persons_account_is_refused()
    {
        using var ws = new TestWorkspace("f5-other");
        var link = Path.Combine(ws.Pictures, "innocent-looking");
        ws.MakeJunction(link, ws.Other);

        var check = ScanRoots.Check(link, ws.Fs, ws.Policy);

        Assert.False(check.Ok);
        Assert.Contains("shortcut", check.Problem);
        Assert.Contains("another person", check.Problem);
    }

    [Fact]
    public void Finding5_Folder_link_a_picked_folder_that_really_leads_to_Google_Drive_is_refused()
    {
        using var ws = new TestWorkspace("f5-google");
        var link = Path.Combine(ws.Pictures, "my-photos");
        ws.MakeJunction(link, ws.FakeGoogleDrive);

        var check = ScanRoots.Check(link, ws.Fs, ws.Policy);

        Assert.False(check.Ok);
        Assert.Contains("Google Drive", check.Problem);
    }

    [Fact]
    public void Finding5_Folder_link_a_picked_folder_that_really_leads_to_an_ordinary_place_is_still_allowed_and_plain_folders_are_unchanged()
    {
        using var ws = new TestWorkspace("f5-ok");
        var link = Path.Combine(ws.Pictures, "to-outside");
        ws.MakeJunction(link, ws.Outside);

        Assert.True(ScanRoots.Check(link, ws.Fs, ws.Policy).Ok);
        Assert.True(ScanRoots.Check(ws.Pictures, ws.Fs, ws.Policy).Ok);
        Assert.False(ScanRoots.Check(ws.Other, ws.Fs, ws.Policy).Ok);
    }

    // ============================================================ 6. Local Google Drive folders get the cloud warning

    private static string VolumeGuidOf(string path)
    {
        var sb = new System.Text.StringBuilder(64);
        Assert.True(GetVolumeNameForVolumeMountPointW(Path.GetPathRoot(path)!, sb, 64));
        var s = sb.ToString();
        return s.Substring(s.IndexOf('{'), s.IndexOf('}') - s.IndexOf('{') + 1);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, System.Text.StringBuilder volumeName, uint length);

    private static long FileIdOf(string folder)
    {
        var psi = new ProcessStartInfo("fsutil", $"file queryfileid \"{folder}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var hex = Regex.Match(text, @"0x([0-9a-fA-F]+)").Groups[1].Value;
        return Convert.ToInt64(hex.Length > 16 ? hex[^16..] : hex, 16);
    }

    /// <summary>Builds a stand-in for Google Drive's own folder list: a small database that names folders by Windows file ID.</summary>
    private static string MakeFakeDriveFs(TestWorkspace ws, params string[] syncedFolders)
    {
        var root = Path.Combine(ws.Root, "FakeDriveFS");
        var account = Path.Combine(root, "account-1");
        Directory.CreateDirectory(account);
        var db = Path.Combine(account, "mirror_sqlite.db");
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            conn.Open();
            void Run(string sql, Action<SqliteCommand>? bind = null)
            {
                using var c = conn.CreateCommand();
                c.CommandText = sql;
                bind?.Invoke(c);
                c.ExecuteNonQuery();
            }
            Run("CREATE TABLE mirror_item (local_stable_id INTEGER PRIMARY KEY, volume TEXT, inode INTEGER)");
            Run("CREATE TABLE root_config (local_stable_id INTEGER)");
            Run("INSERT INTO mirror_item VALUES (999, '{00000000-0000-0000-0000-000000000000}', 12345)");   // an unrelated, unreachable item
            int id = 1;
            foreach (var f in syncedFolders)
            {
                var myId = id++;
                var vol = VolumeGuidOf(f); var inode = FileIdOf(f);
                Run("INSERT INTO mirror_item VALUES ($i, $v, $n)", c => { c.Parameters.AddWithValue("$i", myId); c.Parameters.AddWithValue("$v", vol); c.Parameters.AddWithValue("$n", inode); });
                Run("INSERT INTO root_config VALUES ($i)", c => c.Parameters.AddWithValue("$i", myId));
            }
            Run("INSERT INTO root_config VALUES (999)");
        }
        SqliteConnection.ClearAllPools();
        return root;
    }

    [Fact]
    public void Finding6_Local_Google_Drive_the_folders_Google_Drive_syncs_on_this_PC_are_found_and_get_the_cloud_warning()
    {
        using var ws = new TestWorkspace("f6-gdrive");
        var synced = Path.Combine(ws.Pictures, "Backed up by Google");
        var ordinary = Path.Combine(ws.Pictures, "Just a folder");
        Directory.CreateDirectory(synced); Directory.CreateDirectory(ordinary);
        var driveFs = MakeFakeDriveFs(ws, synced);

        var roots = GoogleDriveFolders.Read(driveFs, ws.Log);

        var found = Assert.Single(roots);
        Assert.Equal("Google Drive", found.Service);
        Assert.Equal(ScanPolicy.Normalize(synced), ScanPolicy.Normalize(found.Path), StringComparer.OrdinalIgnoreCase);
        var cloud = new CloudFolderDetector(roots);
        Assert.Equal("Google Drive", cloud.ServiceOf(Path.Combine(synced, "x.jpg")));          // a photo in a synced folder gets the warning
        Assert.Equal("Google Drive", cloud.ServiceOf(Path.Combine(synced, "Deeper", "y.jpg")));
        Assert.Null(cloud.ServiceOf(Path.Combine(ordinary, "z.jpg")));                          // and an ordinary folder does not
    }

    [Fact]
    public void Finding6_Local_Google_Drive_a_missing_or_unreadable_Google_Drive_folder_list_is_harmless()
    {
        using var ws = new TestWorkspace("f6-bad");
        Assert.Empty(GoogleDriveFolders.Read(Path.Combine(ws.Root, "no-such-folder"), ws.Log));

        var account = Path.Combine(ws.Root, "DriveFSBad", "acct");
        Directory.CreateDirectory(account);
        File.WriteAllText(Path.Combine(account, "mirror_sqlite.db"), "this is not a database");
        Assert.Empty(GoogleDriveFolders.Read(Path.Combine(ws.Root, "DriveFSBad"), ws.Log));   // no crash; it is logged
        Assert.Contains("Google Drive", File.ReadAllText(ws.Log.CurrentFile));

        // the folder list is opened for reading only: a database nobody may change is read just the same
        var synced = Path.Combine(ws.Pictures, "Synced"); Directory.CreateDirectory(synced);
        var driveFs = MakeFakeDriveFs(ws, synced);
        var db = Path.Combine(driveFs, "account-1", "mirror_sqlite.db");
        var before = File.ReadAllBytes(db);
        Assert.Single(GoogleDriveFolders.Read(driveFs, ws.Log));
        Assert.Equal(before, File.ReadAllBytes(db));           // byte for byte unchanged
    }

    [Fact]
    public void Finding6_Local_Google_Drive_the_real_cloud_detector_asks_for_the_Google_Drive_folder_list()
    {
        var src = File.ReadAllText(Path.Combine(AppRoot(), "src", "PhotoTwins.Core", "Sharing", "CloudAndShared.cs"));
        Assert.Contains("GoogleDriveFolders.Read(", src);
    }

    // ============================================================ 7. A power cut can garble the Undo record

    [Fact]
    public void Finding7_Cut_short_line_a_half_written_last_line_does_not_swallow_the_next_record()
    {
        using var ws = new TestWorkspace("f7-journal");
        var path = Path.Combine(ws.Paths.DataDir, "j.jsonl");
        var journal = new UndoJournal(path, ws.Log);
        JournalEntry Make(string id) => new(id, "act", DateTime.Now, @"X:\p\" + id + ".jpg", 10, 5, "AB", "g", @"X:\p\k.jpg", RemoveMethod.Recycle, EntryStatus.Done, null, null);

        journal.Append(Make("one"));
        File.AppendAllText(path, "{\"Id\":\"cut-sho");          // the power failed in the middle of this line: no new line at the end
        journal.Append(Make("two"));
        journal.Append(Make("three"));

        var ids = journal.Entries().Select(e => e.Id).ToArray();
        Assert.Equal(new[] { "one", "two", "three" }, ids);      // both later records are whole and readable
        Assert.NotNull(journal.LoadProblem);                    // the broken line is reported, not hidden
        Assert.EndsWith("\n", File.ReadAllText(path));
    }

    [Fact]
    public void Finding7_Cut_short_line_a_normal_file_and_an_empty_file_are_written_exactly_as_before()
    {
        using var ws = new TestWorkspace("f7-normal");
        var path = Path.Combine(ws.Paths.DataDir, "j2.jsonl");
        var journal = new UndoJournal(path, ws.Log);
        JournalEntry Make(string id) => new(id, "act", DateTime.Now, @"X:\p\" + id + ".jpg", 10, 5, "AB", "g", @"X:\p\k.jpg", RemoveMethod.Folder, EntryStatus.Done, null, null);

        journal.Append(Make("a"));
        journal.Append(Make("b"));

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);                          // no blank lines slipped in
        Assert.Null(journal.LoadProblem);
        Assert.Equal(2, journal.Entries().Count);
    }
}

/// <summary>The two small fixes from the delta review of the seven fixes.</summary>
public class DeltaReviewTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Delta1_Default_bin_size_is_ten_percent_of_the_first_40_GB_plus_five_percent_of_the_rest()
    {
        Assert.Equal(4 * GiB, VolumeRules.DefaultBinCapacity(40 * GiB));
        Assert.Equal(2 * GiB, VolumeRules.DefaultBinCapacity(20 * GiB));                    // small drives: 10%
        Assert.Equal((long)(4 * GiB + 2008 * GiB * 0.05), VolumeRules.DefaultBinCapacity(2048 * GiB));   // a 2 TB drive: about 104 GB, not 205
        Assert.InRange(VolumeRules.DefaultBinCapacity(2048 * GiB)!.Value, 100 * GiB, 110 * GiB);
    }

    [Fact]
    public void Delta1_Default_bin_size_unknown_drive_size_gives_null_so_the_safe_route_is_used()
    {
        Assert.Null(VolumeRules.DefaultBinCapacity(0));
        Assert.Null(VolumeRules.DefaultBinCapacity(-5));
    }

    [Fact]
    public void Delta2_Google_Drive_list_failures_are_written_to_the_log_not_silent()
    {
        var root = Path.GetDirectoryName(TestWorkspace.TestWorkRoot())!;
        var src = File.ReadAllText(Path.Combine(root, "src", "PhotoTwins.Core", "Sharing", "CloudAndShared.cs"));
        Assert.Contains("GoogleDriveFolders.Read(GoogleDriveFolders.DefaultDriveFsRoot(), log)", src);   // the real detector passes the app's log
        var app = File.ReadAllText(Path.Combine(root, "src", "PhotoTwins", "AppServices.cs"));
        Assert.Contains("CloudFolderDetector.ForThisPc(policy, Log)", app);

        // and a list that fails to read really is logged (a stand-in folder, never the live Google Drive data)
        using var ws = new TestWorkspace("delta2");
        var account = Path.Combine(ws.Root, "DriveFSBroken", "acct");
        Directory.CreateDirectory(account);
        File.WriteAllText(Path.Combine(account, "mirror_sqlite.db"), "not a database");
        Assert.Empty(GoogleDriveFolders.Read(Path.Combine(ws.Root, "DriveFSBroken"), ws.Log));
        Assert.Contains("Google Drive", File.ReadAllText(ws.Log.CurrentFile));
    }
}
