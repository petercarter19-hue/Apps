using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Step 3, the part that matters most: how photos leave a folder, and how they come back. Real files, the real Recycle Bin on X:, made-up photos only.</summary>
public class RemovalTests
{
    /// <summary>Pretends the drive has no Recycle Bin, so the "Duplicates to delete" folder route is used.</summary>
    private sealed class NoBin : VolumeRules
    {
        public override string? WhyNotRecyclable(string path, long fileSize) => "the test says this drive has no Recycle Bin";
    }

    /// <summary>Claims the Recycle Bin is fine for everything: used to prove the guard inside the recycling call catches what the pre-check would.</summary>
    private sealed class ClaimsBinIsFine : VolumeRules
    {
        public override string? WhyNotRecyclable(string path, long fileSize) => null;
        public override long? BinCapacityBytes(string root) => long.MaxValue / 4;   // and plenty of room, so only the guard can say no
        public override long? BinUsedBytes(string root) => 0;
    }

    private static DecidedGroup OnlyGroup(ScanResult r) => Assert.Single(r.Decided);

    // ------------------------------------------------------------ the Recycle Bin route (S-BIN)

    [Fact]
    public void S_BIN_a_removed_photo_goes_to_the_real_Recycle_Bin_and_Undo_puts_it_back_exactly()
    {
        using var h = new RemovalHarness("rm-bin");
        var paths = h.Identical(1, "a.jpg", "Sub\\b.jpg");
        var r = h.Scan();
        var g = OnlyGroup(r);
        var keeper = g.Keeper.Path;
        var other = paths.Single(p => !string.Equals(p, keeper, StringComparison.OrdinalIgnoreCase));
        var keeperBefore = FileSnapshot.Of(keeper);
        var otherBefore = FileSnapshot.Of(other);

        var result = h.Run(h.PlanFor(g));

        Assert.Null(result.Refusal);
        var entry = Assert.Single(result.Removed);
        Assert.Equal(RemoveMethod.Recycle, entry.Method);
        Assert.False(File.Exists(other));                                  // gone from its folder
        Assert.NotEmpty(ShellRecycleProbe.FindInBin(other));               // and really sitting in the Recycle Bin
        Assert.Equal(keeperBefore, FileSnapshot.Of(keeper));               // S-KEEPER-UNTOUCHED: identical in every way
        Assert.Equal(1, result.RecycledCount);

        var back = h.Mutations.PutBack(entry);

        Assert.True(back.Ok, back.Message);
        Assert.Equal(EntryStatus.Restored, back.Entry.Status);
        var after = FileSnapshot.Of(other);
        Assert.Equal(otherBefore.Sha, after.Sha);
        Assert.Equal(otherBefore.Size, after.Size);
        Assert.Equal(otherBefore.LastWriteUtc, after.LastWriteUtc);
        Assert.Empty(ShellRecycleProbe.FindInBin(other));                  // out of the bin again
        Assert.Equal(keeperBefore, FileSnapshot.Of(keeper));
    }

    [Fact]
    public void S_BIN_the_real_volume_rules_say_yes_for_a_normal_local_drive_and_no_for_the_unusual_ones()
    {
        var v = new VolumeRules();
        var here = Path.Combine(TestWorkspace.TestWorkRoot(), "x.jpg");
        Assert.Null(v.WhyNotRecyclable(here, 1000));                                                      // this PC's X: drive has a Recycle Bin
        Assert.Contains("network", v.WhyNotRecyclable(@"\\server\share\a.jpg", 1000));
        Assert.Contains("too long", v.WhyNotRecyclable(@"X:\" + new string('a', 250) + ".jpg", 1000));
        Assert.Contains("too big", v.WhyNotRecyclable(here, 500L * 1024 * 1024 * 1024 * 1024));          // bigger than the bin
    }

    [Fact]
    public void S_BIN_a_photo_is_never_deleted_for_good_it_is_in_the_bin_or_in_the_holding_folder_even_on_a_drive_letter_that_has_no_bin()
    {
        using var h = new RemovalHarness("rm-subst", new ClaimsBinIsFine());   // pretend the pre-check was fooled: the guard must still hold
        var target = Path.Combine(h.Ws.Root, "substroot");
        Directory.CreateDirectory(Path.Combine(target, "Pictures"));
        using var drive = new SubstDrive(target);
        h.Ws.Fs.AlsoAllow(drive.Root);
        var pics = drive.Root + "Pictures";
        var bytes = TestPhotoFactory.StandIn(9, 6000);
        var a = TestPhotoFactory.Write(Path.Combine(pics, "a.jpg"), bytes);
        var b = TestPhotoFactory.Write(Path.Combine(pics, "b.jpg"), bytes);
        var scan = h.Scan(pics);
        var g = OnlyGroup(scan);
        var victim = g.Others.Single();
        var sha = Convert.ToHexString(victim.Sha256);

        var result = h.Service.Execute(RemovalPlan.ForGroup(g, new[] { pics }), new Confirmations(), null, CancellationToken.None);

        // The guard inside the recycling call saw that Windows would delete this for good, cancelled it, and the photo went to the holding folder.
        var entry = Assert.Single(result.Removed);
        Assert.Equal(RemoveMethod.Folder, entry.Method);
        Assert.True(File.Exists(entry.Destination!));
        Assert.StartsWith(pics + @"\Duplicates to delete", entry.Destination!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recycle bin refused", File.ReadAllText(h.Ws.Log.CurrentFile));
        var back = h.Mutations.PutBack(entry);
        Assert.True(back.Ok, back.Message);
        Assert.Equal(sha, FileSnapshot.Of(victim.Path).Sha);
    }

    [Fact]
    public void S_NOBIN_FOLDER_on_a_drive_letter_with_no_Recycle_Bin_the_real_rules_choose_the_holding_folder_inside_the_picked_folder()
    {
        using var h = new RemovalHarness("rm-subst2");   // the real rules
        var target = Path.Combine(h.Ws.Root, "substroot2");
        Directory.CreateDirectory(Path.Combine(target, "Pictures", "Trip"));
        using var drive = new SubstDrive(target);
        h.Ws.Fs.AlsoAllow(drive.Root);
        var pics = drive.Root + "Pictures";
        var bytes = TestPhotoFactory.StandIn(9, 6000);
        TestPhotoFactory.Write(Path.Combine(pics, "a.jpg"), bytes);
        TestPhotoFactory.Write(Path.Combine(pics, "Trip", "b.jpg"), bytes);
        Assert.Contains("shortcut", h.Volumes.WhyNotRecyclable(Path.Combine(pics, "a.jpg"), 6000));
        var g = OnlyGroup(h.Scan(pics));
        var victim = g.Others.Single();
        var identityBefore = FileSnapshot.Of(victim.Path).Identity;

        var result = h.Service.Execute(RemovalPlan.ForGroup(g, new[] { pics }), new Confirmations(), null, CancellationToken.None);

        var entry = Assert.Single(result.Removed);
        Assert.Equal(RemoveMethod.Folder, entry.Method);
        var expected = Path.Combine(pics, "Duplicates to delete", victim.Path.Substring(pics.Length + 1));
        Assert.Equal(expected, entry.Destination, ignoreCase: true);
        Assert.Equal(identityBefore, FileSnapshot.Of(entry.Destination!).Identity);   // the very same file: renamed, never copied
        Assert.False(File.Exists(victim.Path));
        Assert.True(h.Mutations.PutBack(entry).Ok);
    }

    // ------------------------------------------------------------ the holding-folder route (S-NOBIN-FOLDER)

    [Fact]
    public void S_NOBIN_FOLDER_without_a_Recycle_Bin_the_photo_is_renamed_into_Duplicates_to_delete_inside_the_picked_folder_and_Undo_returns_it()
    {
        using var h = new RemovalHarness("rm-folder", new NoBin());
        var paths = h.Identical(2, "a.jpg", "Trips\\Beach\\b.jpg");
        var g = OnlyGroup(h.Scan());
        var victim = g.Others.Single();
        var keeperBefore = FileSnapshot.Of(g.Keeper.Path);
        var victimBefore = FileSnapshot.Of(victim.Path);

        var result = h.Run(h.PlanFor(g));

        var entry = Assert.Single(result.Removed);
        Assert.Equal(RemoveMethod.Folder, entry.Method);
        var rel = victim.Path.Substring(h.Ws.Pictures.Length + 1);
        var expected = Path.Combine(h.Ws.Pictures, "Duplicates to delete", rel);
        Assert.Equal(expected, entry.Destination, ignoreCase: true);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(victim.Path));
        Assert.Equal(victimBefore.Identity, FileSnapshot.Of(expected).Identity);   // renamed on the same drive, not copied
        Assert.Equal(keeperBefore, FileSnapshot.Of(g.Keeper.Path));
        Assert.Equal(1, result.MovedToFolderCount);

        // the scan never looks inside the holding folder
        var rescan = h.Scan();
        Assert.Empty(rescan.Decided);

        var back = h.Mutations.PutBack(entry);
        Assert.True(back.Ok, back.Message);
        Assert.Equal(victimBefore.Sha, FileSnapshot.Of(victim.Path).Sha);
        Assert.Equal(victimBefore.Identity, FileSnapshot.Of(victim.Path).Identity);
        Assert.False(File.Exists(expected));
    }

    [Fact]
    public void S_NOBIN_FOLDER_a_name_already_in_the_holding_folder_is_never_overwritten()
    {
        using var h = new RemovalHarness("rm-collide", new NoBin());
        h.Identical(3, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var victim = g.Others.Single();
        var occupied = Path.Combine(h.Ws.Pictures, "Duplicates to delete", victim.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.WriteAllText(occupied, "something already here");

        var result = h.Run(h.PlanFor(g));

        var entry = Assert.Single(result.Removed);
        Assert.EndsWith("(2).jpg", entry.Destination);
        Assert.Equal("something already here", File.ReadAllText(occupied));
    }

    // ------------------------------------------------------------ the journal and recovery (S-UNDO)

    [Fact]
    public void S_UNDO_the_journal_is_written_before_anything_moves_so_a_failure_leaves_the_photo_untouched_and_on_record()
    {
        using var h = new RemovalHarness("rm-journal", new NoBin());
        h.Identical(4, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var victim = g.Others.Single();
        File.WriteAllText(Path.Combine(h.Ws.Pictures, "Duplicates to delete"), "a FILE where the folder should be");   // makes the move impossible
        var before = FileSnapshot.Of(victim.Path);

        var result = h.Run(h.PlanFor(g));

        Assert.Empty(result.Removed);
        var failed = Assert.Single(result.Failed);
        Assert.Equal(victim.Path, failed.File.Path);
        Assert.Equal(before, FileSnapshot.Of(victim.Path));            // nothing moved
        var entry = Assert.Single(h.Journal.Entries());
        Assert.Equal(EntryStatus.Failed, entry.Status);                // it was recorded, then marked failed with a reason
        Assert.NotNull(entry.Reason);
        var lines = File.ReadAllLines(h.Journal.FilePath);
        Assert.Contains(lines, l => l.Contains("\"Status\":\"starting\""));   // "starting" really was written first
        Assert.Contains("Couldn't remove", File.ReadAllText(h.Ws.Log.CurrentFile));
    }

    [Fact]
    public void S_UNDO_after_an_interruption_the_record_is_put_right_for_each_kind_of_leftover()
    {
        using var h = new RemovalHarness("rm-recover", new NoBin());
        var a = h.Identical(5, "a1.jpg", "a2.jpg");   // moved but "starting" never became "done"
        var b = h.Identical(6, "b1.jpg", "b2.jpg");   // never moved
        var c = h.Identical(7, "c1.jpg", "c2.jpg");   // vanished completely
        var groups = h.Scan().Decided;
        PhotoFile Victim(string[] pair) => groups.SelectMany(g => g.Others).Single(f => pair.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
        var va = Victim(a); var vb = Victim(b); var vc = Victim(c);
        JournalEntry Starting(PhotoFile f, string dest) => new(Guid.NewGuid().ToString("N"), "act", DateTime.Now, f.Path, f.Size, f.LastWriteUtcTicks,
            Convert.ToHexString(f.Sha256), "k", "keeper", RemoveMethod.Folder, EntryStatus.Starting, dest, null);
        var destA = Path.Combine(h.Ws.Pictures, "Duplicates to delete", va.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(destA)!);
        File.Move(va.Path, destA);                         // the test plays the part of the interrupted app
        var ea = Starting(va, destA); var eb = Starting(vb, Path.Combine(h.Ws.Pictures, "Duplicates to delete", vb.Name)); var ec = Starting(vc, Path.Combine(h.Ws.Pictures, "Duplicates to delete", vc.Name));
        h.Journal.Append(ea); h.Journal.Append(eb); h.Journal.Append(ec);
        File.Delete(vc.Path);

        var notes = h.Mutations.Reconcile();

        var now = h.Journal.Entries().ToDictionary(e => e.Id);
        Assert.Equal(EntryStatus.Done, now[ea.Id].Status);       // finished: now in the Undo list
        Assert.Equal(EntryStatus.Failed, now[eb.Id].Status);     // nothing had happened
        Assert.Equal(EntryStatus.Unknown, now[ec.Id].Status);    // can't be sure: told to the user
        var note = Assert.Single(notes);
        Assert.Contains("1 had been removed", note);
        Assert.Contains("1 hadn't been touched", note);
        Assert.Contains("1 couldn't be checked", note);
        Assert.True(h.Mutations.PutBack(now[ea.Id]).Ok);          // and the finished one really can be put back
        Assert.Empty(h.Mutations.Reconcile());                    // nothing left to fix
    }

    [Fact]
    public void S_UNDO_an_interrupted_Recycle_Bin_removal_is_recognised_by_finding_the_photo_in_the_bin()
    {
        using var h = new RemovalHarness("rm-recover-bin");
        h.Identical(8, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var result = h.Run(h.PlanFor(g));
        var done = Assert.Single(result.Removed);
        h.Journal.Append(done with { Status = EntryStatus.Starting, Destination = null });   // as if the "done" line was never written

        var notes = h.Mutations.Reconcile();

        Assert.Single(notes);
        Assert.Equal(EntryStatus.Done, h.Journal.Entries().Single().Status);
        Assert.True(h.Mutations.PutBack(h.Journal.Entries().Single()).Ok);
    }

    [Fact]
    public void S_UNDO_a_half_written_last_line_of_the_journal_is_noticed_not_fatal()
    {
        using var h = new RemovalHarness("rm-halfline", new NoBin());
        h.Identical(10, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        h.Run(h.PlanFor(g));
        File.AppendAllText(h.Journal.FilePath, "{\"Id\":\"abc\",\"ActionId\":\"x\",\"Time");   // the power went out mid-line

        var entries = h.Journal.Entries();

        Assert.Single(entries);
        Assert.NotNull(h.Journal.LoadProblem);
        Assert.Contains("unreadable", File.ReadAllText(h.Ws.Log.CurrentFile));
    }

    [Fact]
    public void S_UNDO_putting_back_never_overwrites_and_says_clearly_when_a_photo_is_no_longer_there()
    {
        using var h = new RemovalHarness("rm-undo-edges", new NoBin());
        h.Identical(11, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var victim = g.Others.Single();
        var entry = Assert.Single(h.Run(h.PlanFor(g)).Removed);

        // 1. something new now sits where the photo used to be: both are left alone
        File.WriteAllText(victim.Path, "a different file");
        var blocked = h.Mutations.PutBack(entry);
        Assert.False(blocked.Ok);
        Assert.Contains("already at", blocked.Message);
        Assert.Equal("a different file", File.ReadAllText(victim.Path));
        Assert.True(File.Exists(entry.Destination!));
        File.Delete(victim.Path);

        // 2. put back, but the photo's contents were changed while it sat in the holding folder
        File.AppendAllText(entry.Destination!, "extra bytes");
        var changed = h.Mutations.PutBack(entry);
        Assert.True(changed.Ok);
        Assert.Contains("don't match", changed.Message);
        Assert.True(File.Exists(victim.Path));

        // 3. already put back
        var again = h.Mutations.PutBack(changed.Entry);
        Assert.False(again.Ok);
        Assert.Contains("already", again.Message);
    }

    [Fact]
    public void S_UNDO_a_photo_that_was_taken_out_of_the_holding_folder_or_the_Recycle_Bin_is_reported_as_gone()
    {
        using var h = new RemovalHarness("rm-gone", new NoBin());
        h.Identical(12, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var entry = Assert.Single(h.Run(h.PlanFor(g)).Removed);
        File.Delete(entry.Destination!);   // someone emptied the holding folder

        var r = h.Mutations.PutBack(entry);

        Assert.False(r.Ok);
        Assert.Equal(EntryStatus.Gone, r.Entry.Status);
        Assert.Contains("no longer in the \"Duplicates to delete\" folder", r.Message);

        // a journal line that says "recycled" for a photo that isn't in the bin (it was emptied)
        var ghostPath = Path.Combine(h.Ws.Pictures, "never-recycled.jpg");
        var ghost = entry with { Id = "ghost", Method = RemoveMethod.Recycle, Status = EntryStatus.Done, Destination = null, OriginalPath = ghostPath, OriginalRealPath = ghostPath };
        var r2 = h.Mutations.PutBack(ghost);
        Assert.False(r2.Ok);
        Assert.Contains("no longer in the Recycle Bin", r2.Message);
    }

    [Fact]
    public void A_read_only_or_hidden_photo_is_removed_and_put_back_with_its_attributes_intact()
    {
        using var h = new RemovalHarness("rm-attrs", new NoBin());
        h.Identical(15, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var victim = g.Others.Single();
        File.SetAttributes(victim.Path, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var before = FileSnapshot.Of(victim.Path);

        var entry = Assert.Single(h.Run(h.PlanFor(g)).Removed);
        Assert.False(File.Exists(victim.Path));
        Assert.True(h.Mutations.PutBack(entry).Ok);

        var after = FileSnapshot.Of(victim.Path);
        Assert.Equal(before.Attributes, after.Attributes);   // including the "ready for backup" flag, which Windows would otherwise set on a rename
        Assert.Equal(before.Sha, after.Sha);
        Assert.Equal(before.LastWriteUtc, after.LastWriteUtc);
        File.SetAttributes(victim.Path, FileAttributes.Normal);
    }

    [Fact]
    public void The_ready_for_backup_flag_is_restored_both_ways_for_an_ordinary_photo_too()
    {
        using var h = new RemovalHarness("rm-archive", new NoBin());
        h.Identical(17, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var victim = g.Others.Single();
        File.SetAttributes(victim.Path, FileAttributes.Normal);   // an ordinary photo that is NOT flagged "ready for backup"
        var before = FileSnapshot.Of(victim.Path);
        Assert.Equal(FileAttributes.Normal, before.Attributes);

        var entry = Assert.Single(h.Run(h.PlanFor(g)).Removed);
        Assert.Equal(before.Attributes, FileSnapshot.Of(entry.Destination!).Attributes);   // the flag was not added by the move
        Assert.True(h.Mutations.PutBack(entry).Ok);

        Assert.Equal(before.Attributes, FileSnapshot.Of(victim.Path).Attributes);
    }

    [Fact]
    public void After_Undo_empty_folders_left_in_the_holding_folder_are_tidied_but_only_empty_ones_inside_it()
    {
        using var h = new RemovalHarness("rm-tidy", new NoBin());
        h.Identical(18, "Trips\\Beach\\a.jpg", "Trips\\Beach\\b.jpg", "Trips\\c.jpg");
        var g = OnlyGroup(h.Scan());
        var result = h.Run(h.PlanFor(g));
        Assert.Equal(2, result.Removed.Count);
        var holding = Path.Combine(h.Ws.Pictures, "Duplicates to delete");
        var beachHolding = Path.Combine(holding, "Trips", "Beach");
        var tripsHolding = Path.Combine(holding, "Trips");
        // something else of Pete's sits in the holding folder next to them: it must survive, and so must its folder
        File.WriteAllText(Path.Combine(holding, "Trips", "keep-me.txt"), "mine");
        var inBeach = result.Removed.Where(e => e.Destination!.StartsWith(beachHolding, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var e in result.Removed) Assert.True(h.Mutations.PutBack(e).Ok);

        Assert.False(Directory.Exists(beachHolding) && !Directory.EnumerateFileSystemEntries(beachHolding).Any());   // the emptied sub-folder is gone
        Assert.True(Directory.Exists(tripsHolding));            // it still holds Pete's file
        Assert.True(File.Exists(Path.Combine(tripsHolding, "keep-me.txt")));
        Assert.True(Directory.Exists(holding));                 // the holding folder itself is never removed
        Assert.NotEmpty(inBeach);
        // the picked folders themselves were never touched
        Assert.True(Directory.Exists(Path.Combine(h.Ws.Pictures, "Trips", "Beach")));
    }

    [Fact]
    public void Empty_folders_outside_the_holding_folder_are_never_removed()
    {
        using var h = new RemovalHarness("rm-tidy2", new NoBin());
        h.Identical(19, "a.jpg", "Empty\\b.jpg");
        var g = OnlyGroup(h.Scan());
        var entry = Assert.Single(h.Run(h.PlanFor(g)).Removed);
        // move the Undo target's folder away so the photo's original folder is gone, then look at what survives
        var emptyPictureFolder = Path.Combine(h.Ws.Pictures, "Empty");
        var sibling = Path.Combine(h.Ws.Pictures, "AnotherEmptyFolder"); Directory.CreateDirectory(sibling);

        Assert.True(h.Mutations.PutBack(entry).Ok);

        Assert.True(Directory.Exists(sibling));                 // an unrelated empty folder is left alone
        Assert.True(Directory.Exists(emptyPictureFolder) || File.Exists(entry.OriginalPath));
        Assert.True(File.Exists(entry.OriginalPath));
    }

    [Fact]
    public void A_photo_another_program_is_holding_open_is_reported_not_removed_and_the_rest_carry_on()
    {
        using var h = new RemovalHarness("rm-locked", new NoBin());
        h.Identical(16, "a.jpg", "b.jpg", "c.jpg");
        var g = OnlyGroup(h.Scan());
        var others = g.Others.ToList();
        using var hold = new FileStream(others[0].Path, FileMode.Open, FileAccess.Read, FileShare.None);   // e.g. a photo editor has it open

        var result = h.Run(h.PlanFor(g));

        Assert.Single(result.Removed);
        var left = Assert.Single(result.Dropped);                          // the safety check can't read it, so it is left out and listed
        Assert.Equal(others[0].Path, left.File.Path);
        Assert.Contains("couldn't be read", left.Why);
        Assert.True(File.Exists(others[0].Path));                          // untouched
        Assert.DoesNotContain(h.Journal.Entries(), e => e.OriginalPath == others[0].Path);
    }

    // ------------------------------------------------------------ the keeper is never touched (S-KEEPER-UNTOUCHED, S-LAST)

    [Fact]
    public void S_KEEPER_UNTOUCHED_the_file_layer_itself_refuses_to_touch_the_keeper()
    {
        using var h = new RemovalHarness("rm-keeper", new NoBin());
        h.Identical(13, "a.jpg", "b.jpg");
        var g = OnlyGroup(h.Scan());
        var before = FileSnapshot.Of(g.Keeper.Path);

        Assert.Throws<InvalidOperationException>(() =>
            h.Mutations.Remove(g.Keeper, "act", g.Key, g.Keeper.Path, h.Ws.Pictures, new[] { g.Keeper.Path }));
        Assert.Throws<InvalidOperationException>(() =>
            h.Mutations.Remove(g.Others.Single(), "act", g.Key, g.Others.Single().Path, h.Ws.Pictures, Array.Empty<string>()));   // keeper path equals the target: refused too

        Assert.Equal(before, FileSnapshot.Of(g.Keeper.Path));
        Assert.Empty(h.Journal.Entries());
    }

    [Fact]
    public void S_LAST_every_group_keeps_exactly_one_photo_and_asking_again_has_nothing_to_remove()
    {
        using var h = new RemovalHarness("rm-last", new NoBin());
        h.Identical(14, "a.jpg", "b.jpg", "c.jpg", "d.jpg");
        var g = OnlyGroup(h.Scan());
        Assert.Equal(3, g.Others.Count());

        var result = h.Run(h.PlanFor(g));

        Assert.Equal(3, result.Removed.Count);
        var left = Directory.EnumerateFiles(h.Ws.Pictures, "*.jpg", SearchOption.TopDirectoryOnly).ToList();
        Assert.Single(left);
        Assert.Equal(g.Keeper.Path, left[0], ignoreCase: true);
        g.Forget(result.Removed.Select(e => e.OriginalPath));
        Assert.False(g.StillAGroup);
        var again = h.Run(h.PlanFor(g));
        Assert.NotNull(again.Refusal);
        Assert.Single(Directory.EnumerateFiles(h.Ws.Pictures, "*.jpg", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Many_exact_copy_groups_are_removed_in_one_action_and_every_keeper_is_untouched()
    {
        using var h = new RemovalHarness("rm-bulk", new NoBin());
        var keepers = new List<(string path, FileSnapshot snap)>();
        for (int i = 0; i < 25; i++) h.Identical(100 + i, $"G{i}\\orig.jpg", $"G{i}\\copy.jpg", $"G{i}\\Sub\\x.jpg");
        var scan = h.Scan();
        Assert.Equal(25, scan.Decided.Count);
        foreach (var g in scan.Decided) keepers.Add((g.Keeper.Path, FileSnapshot.Of(g.Keeper.Path)));

        var plan = RemovalPlan.ForAllExactCopies(scan.Decided, h.Roots);
        Assert.Equal(50, plan.PhotoCount);
        var result = h.Run(plan, new Confirmations(BulkExactCopies: true));

        Assert.Null(result.Refusal);
        Assert.Equal(50, result.Removed.Count);
        Assert.Empty(result.Failed);
        foreach (var (path, snap) in keepers) Assert.Equal(snap, FileSnapshot.Of(path));
        Assert.Equal(25, Directory.EnumerateFiles(h.Ws.Pictures, "*.jpg", SearchOption.AllDirectories).Count(f => !f.Contains("Duplicates to delete")));
        Assert.Equal(25, scan.Decided.Count(g => File.Exists(g.Keeper.Path)));
        Assert.Equal(50, h.Journal.Entries().Count(e => e.Status == EntryStatus.Done));
    }

    [Fact]
    public void Stopping_in_the_middle_of_a_removal_leaves_what_was_done_on_record_and_the_rest_untouched()
    {
        using var h = new RemovalHarness("rm-cancel", new NoBin());
        for (int i = 0; i < 6; i++) h.Identical(200 + i, $"G{i}\\a.jpg", $"G{i}\\b.jpg");
        var scan = h.Scan();
        var plan = RemovalPlan.ForAllExactCopies(scan.Decided, h.Roots);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<RemovalProgress>(p => { if (p.Done == 2) cts.Cancel(); });

        var result = h.Service.Execute(plan, new Confirmations(BulkExactCopies: true), progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(2, result.Removed.Count);
        Assert.Equal(4, scan.Decided.SelectMany(g => g.Others).Count(f => File.Exists(f.Path)));
        Assert.Equal(2, h.Journal.Entries().Count(e => e.Status == EntryStatus.Done));
    }
}

/// <summary>Looks in the real Recycle Bin (tests only).</summary>
internal static class ShellRecycleProbe
{
    public static IReadOnlyList<object> FindInBin(string originalPath) =>
        typeof(FileMutations).Assembly.GetType("PhotoTwins.Core.Removal.ShellRecycle")!
            .GetMethod("FindInBin")!.Invoke(null, new object[] { originalPath }) is System.Collections.IEnumerable e ? e.Cast<object>().ToList() : new List<object>();
}
