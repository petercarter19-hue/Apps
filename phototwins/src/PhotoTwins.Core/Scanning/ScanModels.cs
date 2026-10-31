using System.Diagnostics;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Native;

namespace PhotoTwins.Core.Scanning;

public enum ScanPhase { Finding, Reading, Grouping, Done }

public sealed record ScanProgress(
    ScanPhase Phase,
    long PhotosFound,
    long PhotosDone,
    long PhotosTotal,
    /// <summary>Estimated time left for the photos still to be read, or null while that can't be worked out yet.</summary>
    TimeSpan? Remaining,
    bool Paused);

/// <summary>A photo the scan couldn't read, with a plain-English reason. Also written to the log.</summary>
public sealed record Problem(string Path, string Reason);

/// <summary>A folder (or whole picked folder) the scan left out, with the reason.</summary>
public sealed record LeftOut(string Path, string Reason);

public sealed record PhotoFile(string Path, long Size, long LastWriteUtcTicks, byte[] Sha256, FileIdentity Identity, Analysis? Analysis = null, long CreatedUtcTicks = 0)
{
    public Fingerprint? Fingerprint => Analysis?.Fingerprint;
    public PhotoInfo Info => Analysis?.Info ?? PhotoInfo.None;
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Name => System.IO.Path.GetFileName(Path);
    public DateTime LastWriteLocal => new DateTime(LastWriteUtcTicks, DateTimeKind.Utc).ToLocalTime();
}

/// <summary>Photos whose bytes are exactly the same, whatever they're called.</summary>
public sealed class ExactGroup
{
    public IReadOnlyList<PhotoFile> Files { get; }
    public long Size { get; }
    public ExactGroup(IReadOnlyList<PhotoFile> files) { Files = files; Size = files[0].Size; }
    public int ExtraCopies => Files.Count - 1;
    public long FreeableBytes => Size * ExtraCopies;
}

public sealed class ScanResult
{
    /// <summary>False if the scan was stopped early: the groups shown are real but may not be all of them.</summary>
    public bool Completed { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();
    public int PhotosFound { get; init; }
    public int ReadFromDisk { get; init; }
    public int FromCache { get; init; }
    /// <summary>Photos skipped because they exist only in the cloud (never opened, never downloaded).</summary>
    public int SkippedOnline { get; init; }
    public IReadOnlyList<Problem> Problems { get; init; } = Array.Empty<Problem>();
    public IReadOnlyList<LeftOut> LeftOut { get; init; } = Array.Empty<LeftOut>();
    /// <summary>Pictures whose only extras are identical files.</summary>
    public IReadOnlyList<ExactGroup> ExactGroups { get; init; } = Array.Empty<ExactGroup>();
    /// <summary>One picture saved more than once in different ways (resized, re-saved, another type, from a chat app).</summary>
    public IReadOnlyList<SameGroup> SameGroups { get; init; } = Array.Empty<SameGroup>();
    public TimeSpan Elapsed { get; init; }
    /// <summary>Set if the memory of this scan couldn't be saved (disk full, locked). The results themselves are fine.</summary>
    public string? CacheWarning { get; init; }

    /// <summary>Every group with its keeper decided (the star), which photos are in shared and cloud folders, and the user's earlier picks applied.</summary>
    public IReadOnlyList<DecidedGroup> Decided { get; init; } = Array.Empty<DecidedGroup>();

    /// <summary>Different photos that look alike (only when switched on in Settings). Never counted as duplicates in the headline.</summary>
    public IReadOnlyList<DecidedGroup> LookAlikes { get; init; } = Array.Empty<DecidedGroup>();

    /// <summary>Extra photos that could go: for each group, everything except the one photo to keep.</summary>
    public int DuplicateCount => Decided.Count > 0 ? Decided.Sum(g => g.ExtraPhotos) : ExactGroups.Sum(g => g.ExtraCopies) + SameGroups.Sum(g => g.ExtraPhotos);
    public long FreeableBytes => Decided.Count > 0 ? Decided.Sum(g => g.FreeableBytes)
        : ExactGroups.Sum(g => g.FreeableBytes) + SameGroups.Sum(g => g.TotalBytes - g.Versions[0].Files[0].Size);
}

/// <summary>Pause, resume and stop for a running scan. Stop makes everything hurry to a safe halt with the cache saved.</summary>
public sealed class ScanControl
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _running = new(true);
    private readonly Stopwatch _active = new();

    public CancellationToken Token => _cts.Token;
    public bool IsPaused => !_running.IsSet;
    public bool IsStopRequested => _cts.IsCancellationRequested;
    /// <summary>Time actually spent scanning, not counting time paused (used for the time-left estimate).</summary>
    public TimeSpan ActiveElapsed => _active.Elapsed;

    internal void Started() => _active.Start();
    internal void Finished() => _active.Stop();

    public void Pause() { if (!_cts.IsCancellationRequested) { _running.Reset(); _active.Stop(); } }
    public void Resume() { if (!_running.IsSet) { _active.Start(); _running.Set(); } }
    public void Stop() { _cts.Cancel(); _running.Set(); }

    /// <summary>Blocks while paused; throws OperationCanceledException if stopped.</summary>
    public void CheckpointOrWait()
    {
        Token.ThrowIfCancellationRequested();
        if (!_running.IsSet) _running.Wait(Token);
        Token.ThrowIfCancellationRequested();
    }
}
