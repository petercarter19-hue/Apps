using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using PhotoTwins.Core.Cache;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Native;

namespace PhotoTwins.Core.Scanning;

/// <summary>
/// Finds photos in the picked folders and works out which are exact copies (same bytes, any name).
/// Stage 1 only lists folders (no file opened). Stage 2 reads each new or changed photo once to make its
/// SHA-256 fingerprint; anything already in the cache isn't read again.
/// </summary>
public sealed class ScanEngine
{
    private const long PerFileCost = 1536 * 1024;   // time-left estimate counts each file as this many extra bytes of work (opening the picture takes time too)
    private const long MemoryBudget = 600L * 1024 * 1024;   // photos being read at once never hold more than this in memory

    private readonly IFileSystem _fs;
    private readonly PhotoCache _cache;
    private readonly AppLog _log;
    private readonly ScanPolicy _policy;
    private readonly PhotoAnalyzer _analyzer;
    private readonly DecisionContext? _decisions;
    private readonly ThumbnailStore? _thumbs;

    public int MaxParallelReads { get; set; } = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);

    public ScanEngine(IFileSystem fs, PhotoCache cache, AppLog log, ScanPolicy policy, PhotoAnalyzer? analyzer = null, DecisionContext? decisions = null, ThumbnailStore? thumbs = null)
    {
        _fs = fs; _cache = cache; _log = log; _policy = policy; _analyzer = analyzer ?? new PhotoAnalyzer(); _decisions = decisions; _thumbs = thumbs;
    }

    public ScanResult Run(IReadOnlyList<string> pickedFolders, ScanControl control, IProgress<ScanProgress>? progress)
    {
        var clock = Stopwatch.StartNew();
        control.Started();
        long scanId = _cache.BeginScan();
        var problems = new List<Problem>();
        var leftOut = new List<LeftOut>();
        var problemGate = new object();
        void AddProblem(string path, string reason)
        {
            lock (problemGate) problems.Add(new Problem(path, reason));
            _log.Warn($"Couldn't read {path}: {reason}");
        }
        _log.Info($"Scan {scanId} started: {string.Join("; ", pickedFolders)}");

        try
        {
            // ---- Which folders are we really allowed to scan?
            var okRoots = new List<string>();
            foreach (var picked in pickedFolders)
            {
                var chk = ScanRoots.Check(picked, _fs, _policy);
                if (chk.Ok) okRoots.Add(chk.Path);
                else { leftOut.Add(new LeftOut(string.IsNullOrWhiteSpace(chk.Path) ? picked : chk.Path, chk.Problem!)); _log.Warn($"Folder not scanned: {picked}: {chk.Problem}"); }
            }
            var roots = ScanRoots.Simplify(okRoots);

            // ---- Stage 1: list the folders (names and attributes only; nothing is opened)
            var candidates = new List<FileEntry>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skippedOnline = 0;
            var lastEmit = Stopwatch.StartNew();
            foreach (var root in roots)
            {
                string realRoot;
                try { realRoot = _fs.GetRealPath(root); }
                catch (Exception ex) { AddProblem(root, "Couldn't verify this folder: " + Friendly(ex)); continue; }
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    control.CheckpointOrWait();
                    var dir = stack.Pop();
                    try
                    {
                        var real = _fs.GetRealPath(dir);
                        var expected = Path.Combine(realRoot, Path.GetRelativePath(root, dir));
                        var why = _policy.WhyNotAllowedAsRoot(real);
                        if (!string.Equals(ScanPolicy.Normalize(real), ScanPolicy.Normalize(expected), StringComparison.OrdinalIgnoreCase))
                            why = "It no longer leads inside the folder you picked.";
                        if (why != null) { leftOut.Add(new LeftOut(dir, why)); _log.Info($"Folder left out: {dir}: {why}"); continue; }
                    }
                    catch (Exception ex) { AddProblem(dir, "Couldn't verify this folder: " + Friendly(ex)); continue; }
                    var entries = new List<FileEntry>();
                    try { foreach (var e in _fs.Enumerate(dir)) entries.Add(e); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AddProblem(dir, "Couldn't open this folder: " + Friendly(ex)); }

                    foreach (var e in entries)
                    {
                        if (e.IsDirectory)
                        {
                            var why = _policy.WhyLeaveOut(e);
                            if (why != null) { leftOut.Add(new LeftOut(e.FullPath, why)); _log.Info($"Folder left out: {e.FullPath}: {why}"); }
                            else stack.Push(e.FullPath);
                        }
                        else if (PhotoTypes.IsPhoto(e.Name))
                        {
                            if (e.IsLink) { leftOut.Add(new LeftOut(e.FullPath, "It's a shortcut link, which could lead outside the folders you picked.")); }
                            else if (CloudAttributes.IsOnlineOnly(e.Attributes)) skippedOnline++;
                            else if (e.Length == 0) AddProblem(e.FullPath, "The file is empty (0 bytes).");
                            else if (seenPaths.Add(e.FullPath)) candidates.Add(e);
                        }
                    }
                    if (lastEmit.ElapsedMilliseconds > 150)
                    {
                        lastEmit.Restart();
                        progress?.Report(new ScanProgress(ScanPhase.Finding, candidates.Count, 0, 0, null, control.IsPaused));
                    }
                }
            }
            progress?.Report(new ScanProgress(ScanPhase.Finding, candidates.Count, 0, 0, null, control.IsPaused));

            // ---- Stage 2: fingerprint what isn't already remembered
            var cached = _cache.LoadAll();
            var byIdentity = new Dictionary<(uint, ulong, long, long), CachedPhoto>();
            foreach (var c in cached.Values.Where(c => c.Analysis != null))
                byIdentity[(c.Identity.VolumeSerial, c.Identity.FileId, c.Size, c.LastWriteUtcTicks)] = c;

            var photos = new ConcurrentBag<PhotoFile>();
            var hitPaths = new List<string>();
            var toRead = new List<FileEntry>();
            foreach (var c in candidates)
            {
                if (cached.TryGetValue(c.FullPath, out var row) && row.Analysis != null && row.Size == c.Length && row.LastWriteUtcTicks == c.LastWriteUtcTicks)
                {
                    photos.Add(new PhotoFile(c.FullPath, row.Size, row.LastWriteUtcTicks, row.Sha256, row.Identity, row.Analysis, c.CreatedUtcTicks));
                    hitPaths.Add(c.FullPath);
                    if (row.Analysis.Error != null) AddProblem(c.FullPath, row.Analysis.Error);   // still can't be opened as a picture: still listed
                }
                else toRead.Add(c);
            }

            long total = candidates.Count;
            long done = hitPaths.Count;
            long unitsTotal = toRead.Sum(e => e.Length + PerFileCost);
            long unitsDone = 0;
            int readFromDisk = 0;
            int skippedOnlineLate = 0;

            var budget = new ByteBudget(MemoryBudget);
            var pending = new List<CachedPhoto>();
            var pendingGate = new object();
            var flushClock = Stopwatch.StartNew();
            // The cache only makes the next scan faster. If it can't be written, the scan carries on and says so at the end.
            string? cacheWarning = null;
            void Flush()
            {
                CachedPhoto[] batch;
                lock (pendingGate) { batch = pending.ToArray(); pending.Clear(); flushClock.Restart(); }
                try { _cache.Upsert(batch, scanId); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (cacheWarning == null) _log.Error("Couldn't save to the cache", ex);
                    cacheWarning ??= "PhotoTwins couldn't save its memory of this scan (" + ex.Message + "). The results are fine, but the next scan will take as long as this one.";
                }
            }

            var samples = new Queue<(TimeSpan t, long units)>();
            var reportGate = new object();
            TimeSpan? lastRemaining = null;
            void Report(bool force)
            {
                lock (reportGate)
                {
                    if (!force && lastEmit.ElapsedMilliseconds < 150) return;
                    lastEmit.Restart();
                    var t = control.ActiveElapsed;
                    long u = Interlocked.Read(ref unitsDone);
                    if (!control.IsPaused)
                    {
                        samples.Enqueue((t, u));
                        while (samples.Count > 1 && t - samples.Peek().t > TimeSpan.FromSeconds(30)) samples.Dequeue();
                        var first = samples.Peek();
                        double dt = (t - first.t).TotalSeconds;
                        if (dt >= 2 && u > first.units)
                        {
                            double speed = (u - first.units) / dt;
                            lastRemaining = TimeSpan.FromSeconds(Math.Max(0, (unitsTotal - u) / speed));
                        }
                    }
                    long d = Interlocked.Read(ref done);
                    progress?.Report(new ScanProgress(ScanPhase.Reading, candidates.Count, d, total, d >= total ? TimeSpan.Zero : lastRemaining, control.IsPaused));
                }
            }
            Report(true);

            void ReadOne(FileEntry entry)
            {
                control.CheckpointOrWait();
                long counted = 0;
                try
                {
                    // Look again right before opening: a file that has gone online-only since the listing must never be opened.
                    if (CloudAttributes.IsOnlineOnly(_fs.GetAttributes(entry.FullPath))) { Interlocked.Increment(ref skippedOnlineLate); return; }
                    using var open = _fs.OpenRead(entry.FullPath);
                    var info = open.Info;
                    if (CloudAttributes.IsOnlineOnly(info.Attributes)) { Interlocked.Increment(ref skippedOnlineLate); return; }
                    if (info.Size == 0) { AddProblem(entry.FullPath, "The file is empty (0 bytes)."); return; }

                    byte[] sha;
                    Analysis analysis;
                    if (byIdentity.TryGetValue((info.Identity.VolumeSerial, info.Identity.FileId, info.Size, info.LastWriteUtcTicks), out var moved))
                    {
                        sha = moved.Sha256;   // same file, new name or place: no need to read it again
                        analysis = moved.Analysis!;
                    }
                    else if (info.Size > PhotoAnalyzer.MaxBytes)
                    {
                        sha = Hash(open.Stream, info.Size, control, n => { counted += n; Interlocked.Add(ref unitsDone, n); Report(false); });
                        analysis = Analysis.Failed("It is too large (over 400 MB) to compare by its picture, so only exact copies of it are found.");
                        Interlocked.Increment(ref readFromDisk);
                    }
                    else
                    {
                        budget.Acquire(info.Size, control);
                        try
                        {
                            var bytes = ReadAll(open.Stream, info.Size, control, n => { counted += n; Interlocked.Add(ref unitsDone, n); Report(false); });
                            sha = SHA256.HashData(bytes);
                            analysis = _analyzer.Analyze(bytes);
                        }
                        finally { budget.Release(info.Size); }
                        Interlocked.Increment(ref readFromDisk);
                    }

                    var after = HandleInfoReader.Read(open.Stream.SafeFileHandle);
                    if (after.Size != info.Size || after.LastWriteUtcTicks != info.LastWriteUtcTicks)
                    {
                        AddProblem(entry.FullPath, "The file changed while it was being read. Scan again to include it.");
                        return;
                    }
                    if (analysis.Thumb != null)
                    {
                        // the small picture for the cards was made while the photo was open: remember it, and don't carry it around
                        _thumbs?.Put(ThumbnailStore.KeyOf(info.Size, sha), analysis.Thumb);
                        analysis = analysis with { Thumb = null };
                    }
                    if (analysis.Error != null) AddProblem(entry.FullPath, analysis.Error);
                    photos.Add(new PhotoFile(entry.FullPath, info.Size, info.LastWriteUtcTicks, sha, info.Identity, analysis, entry.CreatedUtcTicks));
                    bool flush;
                    lock (pendingGate)
                    {
                        pending.Add(new CachedPhoto(entry.FullPath, info.Identity, info.Size, info.LastWriteUtcTicks, sha, analysis));
                        flush = pending.Count >= 200 || flushClock.ElapsedMilliseconds >= 2000;
                    }
                    if (flush) Flush();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AddProblem(entry.FullPath, Friendly(ex)); }
                finally
                {
                    if (!control.IsStopRequested)
                    {
                        Interlocked.Add(ref unitsDone, PerFileCost + Math.Max(0, entry.Length - counted));
                        Interlocked.Increment(ref done);
                        Report(false);
                    }
                }
            }

            bool stopped = false;
            try
            {
                Parallel.ForEach(toRead,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelReads), CancellationToken = control.Token },
                    ReadOne);
            }
            catch (OperationCanceledException) { stopped = true; }
            catch (AggregateException ae) when (ae.InnerExceptions.All(i => i is OperationCanceledException)) { stopped = true; }
            finally
            {
                Flush();
                try { _cache.MarkSeen(hitPaths, scanId); }
                catch (Exception ex) { _log.Error("Couldn't update the cache", ex); cacheWarning ??= "PhotoTwins couldn't update its memory of earlier scans (" + ex.Message + ")."; }
            }
            if (control.IsStopRequested) stopped = true;
            skippedOnline += skippedOnlineLate;
            Report(true);

            // ---- Stage 3: group the exact copies and the same-photo matches
            progress?.Report(new ScanProgress(ScanPhase.Grouping, candidates.Count, done, total, null, false));
            var matches = new MatchResult();
            List<DecidedGroup> decided = new();
            List<DecidedGroup> lookAlikes = new();
            try
            {
                control.CheckpointOrWait();
                matches = PhotoMatcher.Build(photos.ToList(), control.Token, control.CheckpointOrWait);
                control.CheckpointOrWait();
                decided = GroupDecider.Decide(matches, _decisions?.Shared, _decisions?.Cloud, _decisions?.Picks(), control.CheckpointOrWait);
                control.CheckpointOrWait();
                if (_decisions?.LookAlikes?.Invoke() is { } strictness)
                {
                    var grouped = new HashSet<string>(decided.SelectMany(d => d.Files.Select(f => f.Path)), StringComparer.OrdinalIgnoreCase);
                    var reps = decided.Select(d => d.Keeper).Concat(photos.Where(p => !grouped.Contains(p.Path))).ToList();
                    lookAlikes = GroupDecider.DecideLookAlikes(LookAlikes.Build(reps, strictness, control.Token, control.CheckpointOrWait), _decisions.Shared, _decisions.Cloud, _decisions.Picks(), control.CheckpointOrWait);
                    _log.Info($"Look-alikes ({strictness}): {lookAlikes.Count} groups.");
                }
                control.CheckpointOrWait();
            }
            catch (OperationCanceledException) { stopped = true; matches = new MatchResult(); decided.Clear(); lookAlikes.Clear(); }

            if (control.IsStopRequested) stopped = true;

            if (!stopped && _thumbs != null)
            {
                // keep pictures only for photos that are in a group (the only ones ever shown)
                var shown = new HashSet<string>(decided.Concat(lookAlikes).SelectMany(d => d.Files).Select(f => ThumbnailStore.KeyOf(f.Size, f.Sha256)));
                _thumbs.Remove(photos.Select(p => ThumbnailStore.KeyOf(p.Size, p.Sha256)).Where(k => !shown.Contains(k)).Distinct().ToList());
            }

            if (!stopped)
            {
                int dropped = 0;
                try { dropped = _cache.RemoveUnseenUnder(roots, scanId); _cache.CompleteScan(); }
                catch (Exception ex) { _log.Error("Couldn't tidy the cache", ex); cacheWarning ??= "PhotoTwins couldn't update its memory of earlier scans (" + ex.Message + ")."; }
                _log.Info($"Scan {scanId} finished: {candidates.Count} photos, {readFromDisk} read, {hitPaths.Count} from cache, {matches.ExactGroups.Count} groups of exact copies, {matches.SameGroups.Count} of the same photo, {problems.Count} problems, {dropped} forgotten.");
            }
            else _log.Info($"Scan {scanId} stopped early: {done} of {total} photos done.");

            progress?.Report(new ScanProgress(ScanPhase.Done, candidates.Count, done, total, null, false));
            return new ScanResult
            {
                Completed = !stopped,
                Roots = roots,
                PhotosFound = candidates.Count,
                ReadFromDisk = readFromDisk,
                FromCache = hitPaths.Count,
                SkippedOnline = skippedOnline,
                Problems = problems.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                LeftOut = leftOut,
                ExactGroups = matches.ExactGroups,
                SameGroups = matches.SameGroups,
                Decided = decided,
                LookAlikes = lookAlikes,
                Elapsed = clock.Elapsed,
                CacheWarning = cacheWarning,
            };
        }
        catch (OperationCanceledException)
        {
            // Stopped while still listing folders: nothing was read yet, the next scan starts over cheaply.
            _log.Info($"Scan {scanId} stopped while finding photos.");
            return new ScanResult { Completed = false, Roots = pickedFolders, Problems = problems, LeftOut = leftOut, Elapsed = clock.Elapsed };
        }
        catch (Exception ex)
        {
            _log.Error($"Scan {scanId} failed", ex);
            throw;
        }
        finally { control.Finished(); }
    }

    private static byte[] Hash(Stream s, long expectedSize, ScanControl control, Action<int> bytesRead)
    {
        using var sha = SHA256.Create();
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long total = 0;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                control.CheckpointOrWait();
                sha.TransformBlock(buf, 0, n, null, 0);
                total += n;
                bytesRead(n);
            }
            if (total != expectedSize) throw new IOException("The file changed while it was being read.");
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return sha.Hash!;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static byte[] ReadAll(Stream s, long size, ScanControl control, Action<int> bytesRead)
    {
        var bytes = new byte[size];
        int got = 0;
        while (got < size)
        {
            control.CheckpointOrWait();
            int n = s.Read(bytes, got, (int)Math.Min(1 << 20, size - got));
            if (n == 0) throw new IOException("The file changed while it was being read.");
            got += n;
            bytesRead(n);
        }
        if (s.ReadByte() != -1) throw new IOException("The file changed while it was being read.");
        return bytes;
    }

    public static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows wouldn't let PhotoTwins open it (no permission).",
        FileNotFoundException or DirectoryNotFoundException => "It was moved or deleted while the scan was running.",
        PathTooLongException => "The file's path is too long for Windows.",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "Another program is using it right now.",
        IOException io => io.Message,
        _ => ex.Message,
    };
}

/// <summary>Keeps the photos being read at once from using too much memory: a photo waits until there is room.</summary>
internal sealed class ByteBudget
{
    private readonly long _limit;
    private long _used;
    private readonly object _gate = new();
    public ByteBudget(long limit) { _limit = limit; }

    public void Acquire(long n, ScanControl control)
    {
        lock (_gate)
        {
            while (_used > 0 && _used + n > _limit)
            {
                control.Token.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 100);
            }
            _used += n;
        }
    }

    public void Release(long n)
    {
        lock (_gate) { _used -= n; Monitor.PulseAll(_gate); }
    }
}
