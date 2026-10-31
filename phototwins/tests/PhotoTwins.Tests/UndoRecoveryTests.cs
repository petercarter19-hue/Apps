using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Removal;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

public class UndoRecoveryTests
{
    private sealed class FailStatusJournal(string path, AppLog log, string status) : UndoJournal(path, log)
    {
        public override void Append(JournalEntry entry)
        {
            if (entry.Status == status) throw new IOException("test journal write failure");
            base.Append(entry);
        }
    }

    [Fact]
    public void A_failed_restore_intent_leaves_the_photo_in_the_holding_folder()
    {
        using var h = new RemovalHarness("undo-intent-fail", new NoBinRules());
        h.Identical(907, "a.jpg", "b.jpg");
        var entry = Assert.Single(h.Run(h.PlanFor(Assert.Single(h.Scan().Decided))).Removed);
        var journal = new FailStatusJournal(h.Journal.FilePath, h.Ws.Log, "restoring");
        var mutations = new FileMutations(h.Ws.Fs, h.Ws.Log, journal, h.Volumes);

        var result = mutations.PutBack(entry);

        Assert.False(result.Ok);
        Assert.True(File.Exists(entry.Destination));
        Assert.False(File.Exists(entry.OriginalPath));
        Assert.Equal(EntryStatus.Done, Assert.Single(h.Journal.Entries()).Status);
    }

    [Fact]
    public void A_final_journal_failure_reports_the_restore_and_reconciles_it_after_restart()
    {
        using var h = new RemovalHarness("undo-finish-fail", new NoBinRules());
        h.Identical(908, "a.jpg", "b.jpg");
        var entry = Assert.Single(h.Run(h.PlanFor(Assert.Single(h.Scan().Decided))).Removed);
        var journal = new FailStatusJournal(h.Journal.FilePath, h.Ws.Log, EntryStatus.Restored);
        var mutations = new FileMutations(h.Ws.Fs, h.Ws.Log, journal, h.Volumes);

        var result = mutations.PutBack(entry);

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(entry.OriginalPath));
        Assert.False(File.Exists(entry.Destination));
        Assert.Equal("restoring", Assert.Single(h.Journal.Entries()).Status);
        new FileMutations(h.Ws.Fs, h.Ws.Log, h.Journal, h.Volumes).Reconcile();
        Assert.Equal(EntryStatus.Restored, Assert.Single(h.Journal.Entries()).Status);
    }

    [Theory]
    [InlineData("source", EntryStatus.Done)]
    [InlineData("original", EntryStatus.Restored)]
    [InlineData("both", EntryStatus.Unknown)]
    [InlineData("both-changed", EntryStatus.Unknown)]
    [InlineData("neither", EntryStatus.Unknown)]
    [InlineData("changed", EntryStatus.Unknown)]
    public void Interrupted_restore_is_reconciled_without_moving_anything(string situation, string expected)
    {
        using var h = new RemovalHarness("undo-interrupted", new NoBinRules());
        h.Identical(909, "a.jpg", "b.jpg");
        var entry = Assert.Single(h.Run(h.PlanFor(Assert.Single(h.Scan().Decided))).Removed);
        h.Journal.Append(entry with { Status = "restoring" });
        if (situation is "original" or "changed") File.Move(entry.Destination!, entry.OriginalPath);
        if (situation is "both" or "both-changed") File.Copy(entry.Destination!, entry.OriginalPath);
        if (situation == "both-changed") File.WriteAllText(entry.Destination!, "a different synthetic photo");
        if (situation == "neither") File.Move(entry.Destination!, Path.Combine(h.Ws.Outside, "saved.jpg"));
        if (situation == "changed") File.WriteAllText(entry.OriginalPath, "changed synthetic photo");
        bool originalBefore = File.Exists(entry.OriginalPath), sourceBefore = File.Exists(entry.Destination);

        h.Mutations.Reconcile();

        Assert.Equal(expected, Assert.Single(h.Journal.Entries()).Status);
        Assert.Equal(originalBefore, File.Exists(entry.OriginalPath));
        Assert.Equal(sourceBefore, File.Exists(entry.Destination));
    }
}
