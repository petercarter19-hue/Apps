using System.Security.Cryptography;
using PhotoTwins.Core.Scanning;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>Step 1: finding exact copies, the cache, pause, stop and continue.</summary>
public class ScanTests
{
    [Fact]
    public void Exact_copies_are_found_whatever_they_are_called_and_wherever_they_are()
    {
        using var ws = new TestWorkspace("exact");
        var a = TestPhotoFactory.Jpeg(1);
        ws.Photo("IMG_0001.jpg", a);
        ws.Photo("holiday cake.jpg", a);
        ws.Photo("Sub\\Deeper\\IMG_0001 (1).jpg", a);
        ws.Photo("other.jpg", TestPhotoFactory.Jpeg(2));
        ws.Photo("pic.png", TestPhotoFactory.Png(3));

        var r = ws.Scan();

        Assert.True(r.Completed);
        Assert.Equal(5, r.PhotosFound);
        var g = Assert.Single(r.ExactGroups);
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(2, g.ExtraCopies);
        Assert.Equal(a.Length * 2L, g.FreeableBytes);
        Assert.Equal(2, r.DuplicateCount);
        Assert.Equal(a.Length * 2L, r.FreeableBytes);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Every_photo_type_is_picked_up_and_other_files_ignored()
    {
        using var ws = new TestWorkspace("types");
        ws.Photo("a.JPG", TestPhotoFactory.Jpeg(1));
        ws.Photo("b.jpeg", TestPhotoFactory.Jpeg(2));
        ws.Photo("c.png", TestPhotoFactory.Png(3));
        ws.Photo("d.bmp", TestPhotoFactory.Bmp(4));
        ws.Photo("e.tif", TestPhotoFactory.Tiff(5));
        ws.Photo("f.tiff", TestPhotoFactory.Tiff(6));
        ws.Photo("g.heic", TestPhotoFactory.StandIn(7));
        ws.Photo("h.WEBP", TestPhotoFactory.StandIn(8));
        ws.Photo("notes.txt", TestPhotoFactory.StandIn(9));
        ws.Photo("movie.mp4", TestPhotoFactory.StandIn(10));

        var r = ws.Scan();

        Assert.Equal(8, r.PhotosFound);
        Assert.Empty(r.ExactGroups);
    }

    [Fact]
    public void Same_size_but_different_bytes_is_not_an_exact_copy()
    {
        using var ws = new TestWorkspace("samesize");
        ws.Photo("one.heic", TestPhotoFactory.StandIn(1, 5000));
        ws.Photo("two.heic", TestPhotoFactory.StandIn(2, 5000));

        var r = ws.Scan();

        Assert.Equal(2, r.PhotosFound);
        Assert.Empty(r.ExactGroups);
    }

    [Fact]
    public void Fingerprint_is_the_real_sha256_of_the_file()
    {
        using var ws = new TestWorkspace("sha");
        var bytes = TestPhotoFactory.Jpeg(11);
        ws.Photo("a.jpg", bytes);
        ws.Photo("b.jpg", bytes);

        var r = ws.Scan();

        var g = Assert.Single(r.ExactGroups);
        Assert.Equal(SHA256.HashData(bytes), g.Files[0].Sha256);
    }

    [Fact]
    public void Two_paths_to_the_same_file_count_once_so_nothing_is_called_a_copy_of_itself()
    {
        using var ws = new TestWorkspace("hardlink");
        var a = ws.Photo("original.jpg", TestPhotoFactory.Jpeg(1));
        ws.MakeHardLink(Path.Combine(ws.Pictures, "second name.jpg"), a);

        var r = ws.Scan();

        Assert.Equal(2, r.PhotosFound);
        Assert.Empty(r.ExactGroups);   // removing one name would free nothing, so it is not a duplicate
    }

    [Fact]
    public void Second_scan_reads_nothing_because_everything_is_remembered()
    {
        using var ws = new TestWorkspace("cache");
        var a = TestPhotoFactory.Jpeg(1);
        ws.Photo("a.jpg", a); ws.Photo("b.jpg", a); ws.Photo("c.jpg", TestPhotoFactory.Jpeg(2));

        var first = ws.Scan();
        Assert.Equal(3, first.ReadFromDisk);
        Assert.Equal(0, first.FromCache);
        while (ws.Fs.Opened.TryDequeue(out _)) { }

        var second = ws.Scan();

        Assert.Equal(0, second.ReadFromDisk);
        Assert.Equal(3, second.FromCache);
        Assert.Empty(ws.Fs.Opened);   // no photo was even opened
        Assert.Single(second.ExactGroups);
    }

    [Fact]
    public void A_photo_that_changed_is_read_again()
    {
        using var ws = new TestWorkspace("changed");
        var a = TestPhotoFactory.Jpeg(1);
        var pa = ws.Photo("a.jpg", a); ws.Photo("b.jpg", a);
        Assert.Single(ws.Scan().ExactGroups);

        File.WriteAllBytes(pa, TestPhotoFactory.Jpeg(99));   // a different picture saved over a.jpg
        File.SetLastWriteTimeUtc(pa, DateTime.UtcNow.AddHours(1));
        var r = ws.Scan();

        Assert.Equal(1, r.ReadFromDisk);
        Assert.Empty(r.ExactGroups);
    }

    [Fact]
    public void A_moved_or_renamed_photo_is_recognised_without_reading_it_again()
    {
        using var ws = new TestWorkspace("moved");
        var a = TestPhotoFactory.Jpeg(1);
        var pa = ws.Photo("a.jpg", a); ws.Photo("b.jpg", a);
        ws.Scan();

        File.Move(pa, Path.Combine(ws.Pictures, "renamed.jpg"));   // the test itself renames its own test photo
        var r = ws.Scan();

        Assert.Equal(0, r.ReadFromDisk);
        Assert.Single(r.ExactGroups);
        Assert.Contains(r.ExactGroups[0].Files, f => f.Name == "renamed.jpg");
    }

    [Fact]
    public void Photos_that_are_gone_are_forgotten_after_a_finished_scan()
    {
        using var ws = new TestWorkspace("forget");
        var a = ws.Photo("a.jpg", TestPhotoFactory.Jpeg(1)); ws.Photo("b.jpg", TestPhotoFactory.Jpeg(2));
        ws.Scan();
        Assert.Equal(2, ws.Cache.RowCount);

        File.Delete(a);   // the test removes its own test photo
        var r = ws.Scan();

        Assert.Equal(1, r.PhotosFound);
        Assert.Equal(1, ws.Cache.RowCount);
    }

    [Fact]
    public void Progress_counts_up_to_the_total_and_ends_done()
    {
        using var ws = new TestWorkspace("progress");
        for (int i = 0; i < 25; i++) ws.Photo($"p{i}.jpg", TestPhotoFactory.Jpeg(i));
        var seen = new List<ScanProgress>();

        var r = ws.NewEngine().Run(new[] { ws.Pictures }, new ScanControl(), new SyncProgress<ScanProgress>(p => { lock (seen) seen.Add(p); }));

        Assert.Contains(seen, p => p.Phase == ScanPhase.Finding);
        Assert.Contains(seen, p => p.Phase == ScanPhase.Reading);
        var last = seen[^1];
        Assert.Equal(ScanPhase.Done, last.Phase);
        Assert.Equal(25, last.PhotosDone);
        Assert.Equal(25, last.PhotosTotal);
        Assert.Equal(25, r.PhotosFound);
        var reading = seen.Where(p => p.Phase == ScanPhase.Reading).Select(p => p.PhotosDone).ToList();
        Assert.Equal(reading.OrderBy(x => x), reading);   // never goes backwards
    }

    [Fact]
    public async Task Pause_really_stops_the_work_and_resume_finishes_it()
    {
        using var ws = new TestWorkspace("pause");
        var shared = TestPhotoFactory.Jpeg(1);
        for (int i = 0; i < 40; i++) ws.Photo($"p{i}.jpg", i % 10 == 0 ? shared : TestPhotoFactory.Jpeg(100 + i));
        var control = new ScanControl();
        var engine = ws.NewEngine(); engine.MaxParallelReads = 2;
        long lastDone = 0;
        int opens = 0;
        ws.Fs.BeforeOpen = _ => { if (Interlocked.Increment(ref opens) == 6) control.Pause(); };

        var task = Task.Run(() => engine.Run(new[] { ws.Pictures }, control, new SyncProgress<ScanProgress>(p =>
        {
            if (p.Phase == ScanPhase.Reading) Volatile.Write(ref lastDone, p.PhotosDone);
        })));

        SpinWait.SpinUntil(() => control.IsPaused, 10000);
        Thread.Sleep(600);
        long d1 = Volatile.Read(ref lastDone);
        Thread.Sleep(600);
        Assert.Equal(d1, Volatile.Read(ref lastDone));   // nothing moved while paused
        Assert.False(task.IsCompleted);
        Assert.True(d1 < 40);

        control.Resume();
        var r = await task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(r.Completed);
        Assert.Equal(40, r.PhotosFound);
        Assert.Equal(4, Assert.Single(r.ExactGroups).Files.Count);
    }

    [Fact]
    public void Stop_saves_progress_and_continuing_picks_up_from_the_cache()
    {
        using var ws = new TestWorkspace("stop");
        var shared = TestPhotoFactory.Jpeg(1);
        for (int i = 0; i < 60; i++) ws.Photo($"p{i}.jpg", i % 20 == 0 ? shared : TestPhotoFactory.Jpeg(100 + i));
        var control = new ScanControl();
        var engine = ws.NewEngine(); engine.MaxParallelReads = 1;
        int opens = 0;
        ws.Fs.BeforeOpen = _ => { if (Interlocked.Increment(ref opens) == 25) control.Stop(); };

        var first = engine.Run(new[] { ws.Pictures }, control, null);

        Assert.False(first.Completed);
        Assert.True(ws.Cache.LastScanUnfinished);   // so the next launch can offer "Continue"
        Assert.InRange(first.ReadFromDisk, 1, 59);

        ws.Fs.BeforeOpen = null;
        var second = ws.NewEngine().Run(new[] { ws.Pictures }, new ScanControl(), null);

        Assert.True(second.Completed);
        Assert.False(ws.Cache.LastScanUnfinished);
        Assert.True(second.FromCache >= first.ReadFromDisk - 2, "work done before the stop is not repeated");
        Assert.Equal(60, second.FromCache + second.ReadFromDisk);
        Assert.Equal(3, Assert.Single(second.ExactGroups).Files.Count);
    }

    [Fact]
    public async Task Stopping_while_paused_does_not_hang()
    {
        using var ws = new TestWorkspace("pausestop");
        for (int i = 0; i < 30; i++) ws.Photo($"p{i}.jpg", TestPhotoFactory.Jpeg(i));
        var control = new ScanControl();
        ws.Fs.BeforeOpen = _ => control.Pause();
        var task = Task.Run(() => ws.NewEngine().Run(new[] { ws.Pictures }, control, null));
        SpinWait.SpinUntil(() => control.IsPaused, 10000);
        Thread.Sleep(300);

        control.Stop();

        var r = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(r.Completed);
    }

    [Fact]
    public void Stopping_while_still_listing_folders_is_safe()
    {
        using var ws = new TestWorkspace("stoplisting");
        for (int i = 0; i < 5; i++) ws.Photo($"d{i}\\p.jpg", TestPhotoFactory.Jpeg(i));
        var control = new ScanControl();
        control.Stop();

        var r = ws.NewEngine().Run(new[] { ws.Pictures }, control, null);

        Assert.False(r.Completed);
        Assert.Empty(ws.Fs.Opened);
    }
}
