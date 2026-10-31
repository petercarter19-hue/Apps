using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Removal;

public sealed record RemovalProgress(int Done, int Total, string Current);

public sealed class RemovalResult
{
    /// <summary>Set when the safety gate refused the whole action. Nothing was removed.</summary>
    public string? Refusal { get; init; }
    public string ActionId { get; init; } = "";
    public IReadOnlyList<JournalEntry> Removed { get; init; } = Array.Empty<JournalEntry>();
    public IReadOnlyList<(PhotoFile File, string Why)> Failed { get; init; } = Array.Empty<(PhotoFile, string)>();
    public IReadOnlyList<DroppedFile> Dropped { get; init; } = Array.Empty<DroppedFile>();
    public bool Cancelled { get; init; }

    public int RecycledCount => Removed.Count(e => e.Method == RemoveMethod.Recycle);
    public int MovedToFolderCount => Removed.Count(e => e.Method == RemoveMethod.Folder);
    public long BytesRemoved => Removed.Sum(e => e.Size);
}

/// <summary>Runs a removal plan: the safety gate first, then each photo through <see cref="FileMutations"/>, the only code that changes files.</summary>
public sealed class RemovalService
{
    private readonly SafetyGate _gate;
    private readonly FileMutations _mutations;
    private readonly AppLog _log;

    public RemovalService(SafetyGate gate, FileMutations mutations, AppLog log)
    {
        _gate = gate; _mutations = mutations; _log = log;
    }

    public SafetyGate Gate => _gate;

    public RemovalResult Execute(RemovalPlan plan, Confirmations confirmed, IProgress<RemovalProgress>? progress, CancellationToken cancel)
    {
        var gate = _gate.Check(plan, confirmed);
        if (!gate.Allowed)
        {
            _log.Warn("Removal refused: " + gate.Refusal);
            return new RemovalResult { Refusal = gate.Refusal, Dropped = gate.Dropped };
        }

        var actionId = Guid.NewGuid().ToString("N");
        var protectedPaths = plan.Groups.Select(g => g.Keeper.Path).ToList();
        int total = gate.Groups.Sum(g => g.Targets.Count), done = 0;
        var removed = new List<JournalEntry>();
        var failed = new List<(PhotoFile, string)>();
        var dropped = gate.Dropped.ToList();
        var ledger = new BinLedger(_mutations.Volumes);   // running total of what this action puts in each drive's Recycle Bin
        bool cancelled = false;
        _log.Info($"Removal {actionId}: {total} photos in {gate.Groups.Count} group(s), kind {plan.Kind}.");

        foreach (var ag in gate.Groups)
        {
            string? keeperTrouble = null;
            foreach (var t in ag.Targets)
            {
                if (cancel.IsCancellationRequested) { cancelled = true; break; }
                progress?.Report(new RemovalProgress(done, total, t.Name));
                if (cancel.IsCancellationRequested) { cancelled = true; break; }   // asked to stop while we were reporting

                // Look again, right now: a long removal gives other programs time to change things.
                if (keeperTrouble != null) { dropped.Add(new DroppedFile(t, keeperTrouble)); done++; continue; }
                var why = _gate.Recheck(ag, t, gate.RealRoots, out bool keeperProblem);
                if (why != null)
                {
                    var text = $"Left alone, because {why}.";
                    dropped.Add(new DroppedFile(t, char.ToUpper(text[0]) + text[1..]));
                    if (keeperProblem) keeperTrouble = $"Left alone, because {why}.";
                    _log.Warn($"Left {t.Path} alone: {why}");
                    done++;
                    continue;
                }

                MutationResult r;
                try { r = _mutations.Remove(t, actionId, ag.Planned.Group.Key, ag.Planned.Keeper.Path, ag.PickedRoot, protectedPaths, true, ledger); }
                catch (Exception ex)
                {
                    _log.Error("Unexpected failure removing " + t.Path, ex);
                    failed.Add((t, "Unexpected problem: " + ex.Message));
                    done++;
                    continue;
                }
                if (r.Ok) removed.Add(r.Entry); else failed.Add((t, r.Problem ?? "Unknown problem."));
                done++;
            }
            if (cancelled) break;
        }
        progress?.Report(new RemovalProgress(done, total, ""));
        _log.Info($"Removal {actionId} finished: {removed.Count} removed, {failed.Count} failed, {dropped.Count} left out, cancelled={cancelled}.");
        return new RemovalResult { ActionId = actionId, Removed = removed, Failed = failed, Dropped = dropped, Cancelled = cancelled };
    }
}
