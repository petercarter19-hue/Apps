using System.Security.Cryptography;
using System.Windows;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins;

/// <summary>One removed photo in the Undo list.</summary>
public sealed class UndoRowVm : ObservableObject
{
    private readonly UndoViewModel _owner;
    public JournalEntry Entry { get; private set; }

    public UndoRowVm(JournalEntry entry, UndoViewModel owner)
    {
        Entry = entry; _owner = owner;
        PutBackCommand = new RelayCommand(() => _ = _owner.PutBackAsync(Entry), () => CanPutBack && !_owner.IsBusy);
    }

    public RelayCommand PutBackCommand { get; }
    public string NameText => Format.Breakable(Entry.Name);
    public string FolderText => Format.Breakable(Entry.Folder);
    public string SizeText => Format.Size(Entry.Size);
    public bool CanPutBack => Entry.Status == EntryStatus.Done;

    public string WhereText => Entry.Method == RemoveMethod.Recycle ? "In the Recycle Bin" : "In the \"Duplicates to delete\" folder";

    public string StatusText => Entry.Status switch
    {
        EntryStatus.Restored => "Put back" + (string.IsNullOrEmpty(Entry.Reason) ? "" : ". " + Entry.Reason),
        EntryStatus.Gone => "No longer there, so it can't be put back.",
        EntryStatus.Unknown => "Removal was interrupted. Look in the Recycle Bin and the \"Duplicates to delete\" folders.",
        _ => "",
    };
    public Visibility StatusVisibility => StatusText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ButtonVisibility => CanPutBack ? Visibility.Visible : Visibility.Collapsed;
    public double RowOpacity => Entry.Status == EntryStatus.Done ? 1.0 : 0.7;

    public void Update(JournalEntry e)
    {
        Entry = e;
        foreach (var n in new[] { nameof(CanPutBack), nameof(StatusText), nameof(StatusVisibility), nameof(ButtonVisibility), nameof(RowOpacity), nameof(WhereText) }) Raise(n);
        PutBackCommand.Changed();
    }
}

/// <summary>One removal action (a click on "Remove the others", "Remove all exact copies" or "Remove all same-photo copies") and the photos it removed.</summary>
public sealed class UndoActionVm
{
    /// <summary>A very large removal lists only its first photos on screen (so the window stays quick); "Put back all" still restores every one.</summary>
    public const int ShownRows = 200;

    public UndoActionVm(string header, IReadOnlyList<UndoRowVm> allRows, RelayCommand putBackAll)
    {
        Header = header; AllRows = allRows; Rows = allRows.Take(ShownRows).ToList(); PutBackAllCommand = putBackAll;
    }
    public string Header { get; }
    public IReadOnlyList<UndoRowVm> AllRows { get; }
    public IReadOnlyList<UndoRowVm> Rows { get; }
    public RelayCommand PutBackAllCommand { get; }
    public Visibility PutBackAllVisibility => AllRows.Count(r => r.CanPutBack) > 1 ? Visibility.Visible : Visibility.Collapsed;
    public string MoreText => AllRows.Count > ShownRows ? $"…and {AllRows.Count - ShownRows:N0} more. \"Put back all\" puts every one of them back." : "";
    public Visibility MoreVisibility => AllRows.Count > ShownRows ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>What happened on the main screen when photos were put back.</summary>
public sealed record RestoreOutcome(int InGroups, int NotInList);

/// <summary>What a photo that was just put back looks like on disk now, read off the screen thread: its size, time, Windows file ID and SHA-256.</summary>
public sealed record PhotoNow(long Size, long LastWriteUtcTicks, FileIdentity Identity, byte[] Sha256);

/// <summary>A photo that was put back, with what it looks like now (<see cref="Now"/> is null if it couldn't be checked: online-only, changed, unreadable).</summary>
public sealed record RestoredPhoto(JournalEntry Entry, PhotoNow? Now);

/// <summary>The main screen, told when photos are put back so they reappear in their groups. It only updates its lists: nothing is read from disk here.</summary>
public interface IRestoreTarget
{
    RestoreOutcome RestorePhotos(IReadOnlyList<RestoredPhoto> restored);
}

/// <summary>The Undo list: everything PhotoTwins removed, newest first, each with a way to put it back exactly where it was.</summary>
public sealed class UndoViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IRestoreTarget? _target;
    private IReadOnlyList<UndoActionVm> _actions = Array.Empty<UndoActionVm>();
    private string _message = "";
    private bool _busy;
    private TaskCompletionSource? _busyCompletion;

    public UndoViewModel(AppServices services, IRestoreTarget? target = null)
    {
        _s = services; _target = target;
        Refresh();
    }

    public IReadOnlyList<UndoActionVm> Actions { get => _actions; private set { Set(ref _actions, value); Raise(nameof(EmptyVisibility)); } }
    public Visibility EmptyVisibility => _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Message { get => _message; private set { Set(ref _message, value); Raise(nameof(MessageVisibility)); } }
    public Visibility MessageVisibility => _message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); } }
    public Task WaitUntilIdleAsync() => _busyCompletion?.Task ?? Task.CompletedTask;
    /// <summary>How many photos were put back while this window was open (the main screen then suggests a rescan).</summary>
    public int RestoredCount { get; private set; }

    public void Refresh()
    {
        // Only photos that were actually moved are listed; attempts that failed before anything moved are not "removed".
        var entries = _s.Journal.Entries().Where(e => e.Status is EntryStatus.Done or EntryStatus.Restored or EntryStatus.Gone or EntryStatus.Unknown).ToList();
        var actions = entries.GroupBy(e => e.ActionId).Select(g => new { g, when = g.Min(e => e.TimeLocal) }).OrderByDescending(x => x.when).Select(x =>
        {
            var rows = x.g.Select(e => new UndoRowVm(e, this)).ToList();
            var removed = x.g.Count(e => e.Status != EntryStatus.Unknown);
            var header = $"{x.when:MMM d, yyyy h:mm tt} · {Format.Plural(x.g.Count(), "photo", "photos")} removed ({Format.Size(x.g.Sum(e => e.Size))})";
            var putAll = new RelayCommand(() => _ = PutBackAllAsync(rows), () => !IsBusy && rows.Any(r => r.CanPutBack));
            return new UndoActionVm(header, rows, putAll);
        }).ToList();
        Actions = actions;
        if (_s.Journal.LoadProblem != null && _message.Length == 0) Message = _s.Journal.LoadProblem;
    }

    /// <summary>
    /// Looks at a photo that was just put back, off the screen thread. Never reads an online-only file. Null if it can't be vouched for
    /// (online-only, a different size than was removed, unreadable): the main screen then leaves it out of the group.
    /// </summary>
    private PhotoNow? Inspect(JournalEntry e)
    {
        try
        {
            if (CloudAttributes.IsOnlineOnly(_s.Fs.GetAttributes(e.OriginalPath))) return null;
            using var open = _s.Fs.OpenRead(e.OriginalPath);
            if (open.Info.Size != e.Size) return null;
            return new PhotoNow(open.Info.Size, open.Info.LastWriteUtcTicks, open.Info.Identity, SHA256.HashData(open.Stream));
        }
        catch (Exception ex)
        {
            _s.Log.Warn($"Couldn't check {e.OriginalPath} after putting it back: {ex.Message}");
            return null;
        }
    }

    public async Task PutBackAsync(JournalEntry entry)
    {
        var row = _actions.SelectMany(a => a.AllRows).FirstOrDefault(r => r.Entry.Id == entry.Id);
        await PutBackRowsAsync(row == null ? Array.Empty<UndoRowVm>() : new[] { row });
    }

    public Task PutBackAllAsync(IReadOnlyList<UndoRowVm> rows) => PutBackRowsAsync(rows.Where(r => r.CanPutBack).ToList());

    private async Task PutBackRowsAsync(IReadOnlyList<UndoRowVm> rows)
    {
        if (IsBusy || rows.Count == 0) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _busyCompletion = completion;
        IsBusy = true;
        Message = rows.Count == 1 ? "Putting it back…" : $"Putting back {rows.Count} photos…";
        try
        {
            int ok = 0;
            var problems = new List<string>();
            var restored = new List<RestoredPhoto>();
            // the Recycle Bin is read ONCE for the whole action, not once per photo
            var snapshot = rows.Any(r => r.Entry.Method == RemoveMethod.Recycle) ? await Task.Run(() => _s.Mutations.LoadBinSnapshot()) : null;
            foreach (var row in rows)
            {
                var entry = row.Entry;
                // the file work (moving it back, checking it, reading it again for the main screen) all happens off the screen thread
                var (result, now) = await Task.Run(() =>
                {
                    var r = _s.Mutations.PutBack(entry, snapshot);
                    return (r, r.Ok && r.Message == "Put back." ? Inspect(r.Entry) : null);
                });
                row.Update(result.Entry);
                if (result.Ok) { ok++; RestoredCount++; restored.Add(new RestoredPhoto(result.Entry, now)); if (result.Message != "Put back.") problems.Add($"{entry.Name}: {result.Message}"); }
                else problems.Add($"{entry.Name}: {result.Message}");
            }
            string text = "";
            if (ok > 0)
            {
                text = $"Put back {Format.Plural(ok, "photo", "photos")}.";
                if (_target == null) text += " Scan again to see them in the groups.";
                else
                {
                    try
                    {
                        var outcome = _target.RestorePhotos(restored);
                        if (outcome.NotInList == 0) text += ok == 1 ? " It is back in its group on the main screen." : " They are back in their groups on the main screen.";
                        else text += $" {outcome.InGroups} of them are back in their groups on the main screen. The other {outcome.NotInList} aren't on its list any more (it is from a newer scan) or aren't the same photo as when it was scanned: scan again to see {(outcome.NotInList == 1 ? "it" : "them")}.";
                    }
                    catch (Exception ex)
                    {
                        // the photos ARE back on disk; only the picture of them on the main screen couldn't be brought up to date
                        _s.Log.Error("Photos were put back, but the main screen couldn't be updated", ex);
                        text += $" {(ok == 1 ? "It is" : "They are")} back in {(ok == 1 ? "its" : "their")} folder{(ok == 1 ? "" : "s")}, but the main screen couldn't be updated. Scan again to see {(ok == 1 ? "it" : "them")} in the groups. Details were saved to the log.";
                    }
                }
            }
            if (problems.Count > 0) text = (text + " " + string.Join(" ", problems)).Trim();
            Message = text;
        }
        catch (Exception ex)
        {
            _s.Log.Error("Undo failed", ex);
            Message = "Couldn't put the photos back: " + ex.Message + " Details were saved to the log.";
        }
        finally
        {
            IsBusy = false;
            foreach (var a in _actions) a.PutBackAllCommand.Changed();
            foreach (var r in _actions.SelectMany(a => a.Rows)) r.PutBackCommand.Changed();   // (only the rows on screen need their button refreshed)
            completion.TrySetResult();
        }
    }
}
