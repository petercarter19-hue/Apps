using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using System.Text;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Removal;

public sealed record MutationResult(bool Ok, JournalEntry Entry, string? Problem);

/// <summary>
/// THE one place that changes photos on disk. Everything else in the app only reads. Nothing here can permanently delete a photo:
///   - a photo goes to the Recycle Bin when the drive has one, with a guard that cancels any delete Windows would make permanent;
///   - otherwise it is MOVED (a same-drive rename, never copy-then-delete) into a "Duplicates to delete" folder inside the
///     folder the user picked;
///   - every step is written to the undo journal BEFORE it happens, so it can be put back, and recovered if the PC stops midway;
///   - the photo to keep is never touched.
/// </summary>
public sealed class FileMutations
{
    public const string HoldingFolderName = ScanPolicy.HoldingFolderName;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string target, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);

    private sealed class DirectoryLeases : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new();
        public string Pin(string path, bool reparseObject = false)
        {
            // Read/list access participates in Windows sharing checks. Omitting SHARE_DELETE prevents ordinary rename/replacement.
            var handle = CreateFileW(NativePath.Extended(path), 0x81, 3, IntPtr.Zero, 3, 0x02000000u | (reparseObject ? 0x00200000u : 0), IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Couldn't safely hold a folder in place.", new Win32Exception(Marshal.GetLastWin32Error())); }
            _handles.Add(handle);
            var buffer = new StringBuilder(1024);
            var n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (n == 0) throw new IOException("Couldn't verify the held folder.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (n >= buffer.Capacity) { buffer = new StringBuilder((int)n + 1); n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0); if (n == 0 || n >= buffer.Capacity) throw new IOException("Couldn't verify the held folder."); }
            return NativePath.NormalizedResult(buffer.ToString());
        }
        public void Dispose() { for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); }
    }

    private readonly IFileSystem _fs;
    private readonly AppLog _log;
    private readonly UndoJournal _journal;
    private readonly VolumeRules _volumes;

    public VolumeRules Volumes => _volumes;

    public FileMutations(IFileSystem fs, AppLog log, UndoJournal journal, VolumeRules volumes)
    {
        _fs = fs; _log = log; _journal = journal; _volumes = volumes;
    }

    // ------------------------------------------------------------------------------------------ removing

    /// <summary>Removes one photo (recycle, or move to the holding folder). Never permanently deletes. Never throws for a normal failure: it says why.</summary>
    public MutationResult Remove(PhotoFile file, string actionId, string groupKey, string keeperPath, string pickedRoot,
        IReadOnlyCollection<string> protectedPaths, bool tryRecycle = true, BinLedger? ledger = null)
    {
        if (protectedPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("PhotoTwins refused to touch the photo that is being kept.");
        if (string.Equals(file.Path, keeperPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PhotoTwins refused to touch the photo that is being kept.");

        int attrs = -1;
        try { attrs = (int)_fs.GetAttributes(file.Path); } catch { /* the safety check has already looked at it; if not, nothing is restored */ }
        var entry = new JournalEntry(Guid.NewGuid().ToString("N"), actionId, DateTime.Now, file.Path, file.Size, file.LastWriteUtcTicks,
            Convert.ToHexString(file.Sha256), groupKey, keeperPath, RemoveMethod.Recycle, EntryStatus.Starting, null, null, attrs);

        try
        {
            entry = entry with { SelectedRoot = ScanPolicy.Normalize(pickedRoot), RealRoot = _fs.GetRealPath(pickedRoot), OriginalRealPath = _fs.GetRealPath(file.Path), OriginalIdentity = file.Identity };
        }
        catch (Exception ex) { return Fail(entry, "Couldn't verify where the photo is: " + ex.Message, writeStarting: true); }

        string? whyNotRecycle = tryRecycle ? _volumes.WhyNotRecyclable(file.Path, file.Size) : "recycling was not allowed";
        // the whole ACTION is added up too: a bin that overflows makes Windows permanently delete its oldest items
        if (whyNotRecycle == null && ledger != null) whyNotRecycle = ledger.WhyNoRoom(file.Path, file.Size);
        if (whyNotRecycle == null)
        {
            using var leases = new DirectoryLeases();
            try { EnsureTrustedDirectory(entry.SelectedRoot!, entry.RealRoot!, Path.GetDirectoryName(file.Path)!, create: false, leases); }
            catch (Exception ex) { return Fail(entry, "Couldn't verify the photo's folder: " + ex.Message, writeStarting: true); }
            _journal.Append(entry);   // if the record can't be written, this throws BEFORE anything is touched
            RecycleOutcome? outcome = null;
            Exception? recycleError = null;
            try { outcome = ShellRecycle.Recycle(file.Path); }
            catch (Exception ex) { recycleError = ex; }

            if (recycleError != null)
            {
                // Did the photo go anyway? If it is still there, nothing happened.
                if (File.Exists(file.Path)) return Fail(entry, "Windows couldn't move it to the Recycle Bin: " + recycleError.Message);
                _log.Error($"After a Recycle Bin error the photo is gone from {file.Path}", recycleError);
                var unk = SafeUpdate(entry, EntryStatus.Unknown, null, "Windows reported a problem, and the photo is no longer in its folder. Look in the Recycle Bin. " + recycleError.Message);
                return new MutationResult(false, unk, "Windows reported a problem and the photo is no longer in its folder. Look in the Recycle Bin.");
            }
            if (outcome!.Recycled)
            {
                if (File.Exists(file.Path))
                    return Fail(entry, "Windows said it was recycled, but the photo is still where it was.");
                var done = SafeUpdate(entry, EntryStatus.Done, outcome.RecycledPath);
                ledger?.Added(file.Path, file.Size);
                _log.Info($"Recycled {file.Path}");
                return new MutationResult(true, done, null);
            }
            _log.Warn($"Recycle bin refused {file.Path}: {outcome.CancelledBecause}; using the holding folder instead.");
            whyNotRecycle = outcome.CancelledBecause;
        }
        else _log.Info($"{file.Path} can't go to the Recycle Bin because {whyNotRecycle}; moving it to the holding folder.");

        return MoveToHoldingFolder(file, entry with { Method = RemoveMethod.Folder }, pickedRoot, whyNotRecycle);
    }

    private MutationResult MoveToHoldingFolder(PhotoFile file, JournalEntry entry, string pickedRoot, string? whyNotRecycle)
    {
        string dest;
        try { dest = HoldingPath(pickedRoot, file.Path); }
        catch (Exception ex) { return Fail(entry, "Couldn't work out where to move it: " + ex.Message, writeStarting: true); }
        entry = entry with { Destination = dest };
        _journal.Append(entry);   // before anything moves: Starting + where it will go
        try
        {
            using var leases = new DirectoryLeases();
            EnsureTrustedDirectory(entry.SelectedRoot!, entry.RealRoot!, Path.GetDirectoryName(file.Path)!, create: false, leases);
            EnsureTrustedDirectory(entry.SelectedRoot!, entry.RealRoot!, Path.GetDirectoryName(dest)!, create: true, leases);
            dest = FreeName(dest);
            entry = entry with { Destination = dest };
            _journal.Append(entry);
            EnsureTrustedDirectory(entry.SelectedRoot!, entry.RealRoot!, Path.GetDirectoryName(dest)!, create: false, leases);
            // flags 0: a plain rename on the same drive. No "copy if on another drive": that could leave half a photo behind.
            if (!MoveFileExW(NativePath.Extended(file.Path), NativePath.Extended(dest), 0))
                return Fail(entry, "Windows couldn't move it: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (File.Exists(file.Path) || !File.Exists(dest))
                return Fail(entry, "The move didn't finish as expected. Check the photo's folder.");
            RestoreAttributes(dest, entry.Attributes);   // Windows sets "ready for backup" when a file is renamed: put the flags back as they were
            var done = SafeUpdate(entry, EntryStatus.Done, dest, whyNotRecycle);   // why it went to the folder, for the screen
            _log.Info($"Moved {file.Path} to {dest}");
            return new MutationResult(true, done, null);
        }
        catch (Exception ex)
        {
            return Fail(entry, "Couldn't move it: " + ex.Message);
        }
    }

    /// <summary>Create and check each holding-folder component without following an inner shortcut.</summary>
    private void EnsureTrustedDirectory(string pickedRoot, string realRoot, string directory, bool create, DirectoryLeases? leases = null)
    {
        var root = ScanPolicy.Normalize(pickedRoot);
        var full = ScanPolicy.Normalize(directory);
        if (!ScanPolicy.IsSameOrInside(full, root)) throw new IOException("The holding folder is outside the picked folder.");
        if (leases != null)
        {
            var volume = Path.GetPathRoot(root) ?? throw new IOException("The picked folder has no drive root.");
            var cursor = volume;
            leases.Pin(cursor);
            foreach (var part in Path.GetRelativePath(volume, root).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Where(p => p != "."))
            {
                cursor = Path.Combine(cursor, part);
                if (_fs.IsLink(cursor)) leases.Pin(cursor, reparseObject: true);
                var pinned = leases.Pin(cursor);
                if (SamePath(cursor, root) && !SamePath(pinned, realRoot)) throw new IOException("The picked folder now leads somewhere else.");
            }
        }
        if (!SamePath(_fs.GetRealPath(root), realRoot)) throw new IOException("The picked folder now leads somewhere else.");
        var relative = Path.GetRelativePath(root, full);
        var current = root;
        var expected = realRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Where(p => p != "."))
        {
            current = Path.Combine(current, part);
            expected = Path.Combine(expected, part);
            if (Directory.Exists(current) && _fs.IsLink(current)) throw new IOException("The holding folder contains a shortcut link.");
            if (create) Directory.CreateDirectory(current);
            if (leases != null && !SamePath(leases.Pin(current), expected)) throw new IOException("The holding folder does not lead inside the picked folder.");
            if (_fs.IsLink(current) || !SamePath(_fs.GetRealPath(current), expected))
                throw new IOException("The holding folder does not lead inside the picked folder.");
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(ScanPolicy.Normalize(a), ScanPolicy.Normalize(b), StringComparison.OrdinalIgnoreCase);

    private (string root, string realRoot) RestoreAnchor(JournalEntry entry)
    {
        if (entry.SelectedRoot != null && entry.RealRoot != null && entry.OriginalRealPath != null)
        {
            var expected = Path.Combine(entry.RealRoot, Path.GetRelativePath(entry.SelectedRoot, entry.OriginalPath));
            if (!SamePath(expected, entry.OriginalRealPath)) throw new IOException("The original photo location could not be verified.");
            return (entry.SelectedRoot, entry.RealRoot);
        }
        // Older records have no root provenance. Only a plain physical drive path can be checked safely.
        var root = Path.GetPathRoot(entry.OriginalPath) ?? throw new IOException("The original folder could not be verified.");
        if (!SamePath(root, _fs.GetRealPath(root))) throw new IOException("The original folder uses an unverified drive alias.");
        return (root, root);
    }

    private const int KeptFlags = 0x1 | 0x2 | 0x4 | 0x20 | 0x100 | 0x2000;   // read-only, hidden, system, archive, temporary, not-indexed

    /// <summary>Puts a photo's own flags (read-only, hidden, "ready for backup"...) back as they were before it was renamed.</summary>
    private void RestoreAttributes(string path, int attributes)
    {
        if (attributes < 0) return;
        try
        {
            var flags = attributes & KeptFlags;
            File.SetAttributes(path, flags == 0 ? FileAttributes.Normal : (FileAttributes)flags);
        }
        catch (Exception ex) { _log.Warn($"Couldn't restore the flags of {path}: {ex.Message}"); }
    }

    /// <summary>
    /// After a photo comes back out of the "Duplicates to delete" folder, sub-folders it leaves behind that are now EMPTY are removed.
    /// Only empty folders, only inside that holding folder (never the holding folder itself, never a link), and only upward from the
    /// photo's own place; a folder with anything left in it is never touched.
    /// </summary>
    private void TidyEmptyHoldingFolders(string movedFrom)
    {
        try
        {
            var full = Path.GetFullPath(movedFrom);
            var marker = Path.DirectorySeparatorChar + HoldingFolderName + Path.DirectorySeparatorChar;
            int at = full.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            var root = full.Substring(0, at + marker.Length - 1);   // ...\Duplicates to delete
            var dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && ScanPolicy.IsSameOrInside(dir, root) && !string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) { dir = Path.GetDirectoryName(dir); continue; }
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) break;
                if (Directory.EnumerateFileSystemEntries(dir).Any()) break;   // something is still in it: leave it alone
                Directory.Delete(dir, false);                                  // (only ever an empty folder)
                _log.Info($"Removed the empty folder {dir}");
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex) { _log.Warn("Couldn't tidy empty folders in \"Duplicates to delete\": " + ex.Message); }
    }

    /// <summary>
    /// Records the outcome AFTER the photo has already moved. If the record can't be written, the photo still counts as removed
    /// (it IS removed): the "starting" line already on disk lets the next start-up finish the record (see <see cref="Reconcile"/>).
    /// </summary>
    private JournalEntry SafeUpdate(JournalEntry entry, string status, string? destination, string? reason = null)
    {
        try { return _journal.Update(entry, status, destination, reason); }
        catch (Exception ex)
        {
            _log.Error($"The photo {entry.OriginalPath} was moved but the record of it couldn't be finished; it will be finished at the next start-up", ex);
            return entry with { Status = status, Destination = destination ?? entry.Destination, Reason = reason };
        }
    }

    private MutationResult Fail(JournalEntry entry, string why, bool writeStarting = false)
    {
        if (writeStarting) _journal.Append(entry);
        var failed = _journal.Update(entry, EntryStatus.Failed, null, why);
        _log.Warn($"Couldn't remove {entry.OriginalPath}: {why}");
        return new MutationResult(false, failed, why);
    }

    /// <summary>"Duplicates to delete" goes INSIDE the picked folder, keeping the photo's sub-folders, so nothing leaves the folders the user picked.</summary>
    public static string HoldingPath(string pickedRoot, string photoPath)
    {
        var root = ScanPolicy.Normalize(pickedRoot);
        var full = ScanPolicy.Normalize(photoPath);
        if (!ScanPolicy.IsSameOrInside(full, root)) throw new InvalidOperationException("The photo is not inside the picked folder.");
        var rel = full.Substring(root.Length).TrimStart('\\');
        return Path.Combine(root, HoldingFolderName, rel);
    }

    private static string FreeName(string dest)
    {
        if (!File.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest)!;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ------------------------------------------------------------------------------------------ putting back (Undo)

    public sealed record PutBackResult(bool Ok, string Message, JournalEntry Entry);

    /// <summary>Reads the whole Recycle Bin once, to be shared by every photo of one Undo (or recovery) action.</summary>
    public BinSnapshot LoadBinSnapshot() => ShellRecycle.Snapshot();

    /// <summary>True only if the file in the Recycle Bin is exactly the photo the record describes (same size, same contents).</summary>
    private static bool IsThisPhoto(string binPath, JournalEntry e)
    {
        try
        {
            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length != e.Size) return false;
            return string.Equals(Convert.ToHexString(SHA256.HashData(fs)), e.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The item in the bin that really is this photo (the recorded one is tried first), or null.</summary>
    private static BinItem? FindMatching(IReadOnlyList<BinItem> candidates, JournalEntry e) =>
        candidates.OrderByDescending(c => e.Destination != null && string.Equals(c.BinPath, e.Destination, StringComparison.OrdinalIgnoreCase))
                  .FirstOrDefault(c => IsThisPhoto(c.BinPath, e));

    /// <summary>Puts one removed photo back exactly where it was. Never overwrites anything.</summary>
    public PutBackResult PutBack(JournalEntry e, BinSnapshot? snapshot = null)
    {
        if (e.Status != EntryStatus.Done)
            return new PutBackResult(false, e.Status == EntryStatus.Restored ? "It was already put back." : "This one can't be put back.", e);
        try
        {
            var (root, realRoot) = RestoreAnchor(e);
            using var leases = new DirectoryLeases();
            if (File.Exists(e.OriginalPath))
                return new PutBackResult(false, $"A file is already at {e.OriginalPath}, so PhotoTwins left both alone. Rename or move that file first.", e);

            if (e.Method == RemoveMethod.Folder)
            {
                if (e.Destination == null || !File.Exists(e.Destination))
                {
                    var gone = _journal.Update(e, EntryStatus.Gone, null, "No longer in the \"Duplicates to delete\" folder.");
                    return new PutBackResult(false, "It is no longer in the \"Duplicates to delete\" folder, so it can't be put back.", gone);
                }
                EnsureTrustedDirectory(root, realRoot, Path.GetDirectoryName(e.Destination)!, create: false, leases);
                if (_fs.IsLink(e.Destination) || !SamePath(_fs.GetRealPath(e.Destination), Path.Combine(realRoot, Path.GetRelativePath(root, e.Destination))))
                    throw new IOException("The held photo no longer leads to its recorded location.");
                if (CloudAttributes.IsOnlineOnly(_fs.GetAttributes(e.Destination))) throw new IOException("The held photo is online-only and cannot be checked.");
                using (var held = _fs.OpenRead(e.Destination))
                {
                    if (e.OriginalIdentity is { } identity)
                    {
                        if (held.Info.Identity != identity) throw new IOException("The held photo was replaced by a different file.");
                    }
                    else if (held.Info.Size != e.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(held.Stream)), e.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The held photo doesn't match the older removal record.");
                }
                EnsureTrustedDirectory(root, realRoot, Path.GetDirectoryName(e.OriginalPath)!, create: true, leases);
                _journal.Update(e, EntryStatus.Restoring);
                if (!MoveFileExW(NativePath.Extended(e.Destination), NativePath.Extended(e.OriginalPath), 0))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    _journal.Update(e, EntryStatus.Done);
                    return new PutBackResult(false, "Windows couldn't move it back: " + error, e);
                }
            }
            else
            {
                var items = (snapshot ?? ShellRecycle.Snapshot()).Find(e.OriginalPath);
                var item = FindMatching(items, e);   // never "something with the same name": it must BE this photo
                if (item == null)
                {
                    var why = items.Count == 0 ? "No longer in the Recycle Bin." : "Only a different file with the same name is in the Recycle Bin.";
                    var gone = _journal.Update(e, EntryStatus.Gone, null, why);
                    return new PutBackResult(false, items.Count == 0
                        ? "It is no longer in the Recycle Bin (it may have been emptied), so it can't be put back."
                        : "The Recycle Bin only has a different file with the same name, not this photo, so nothing was restored.", gone);
                }
                EnsureTrustedDirectory(root, realRoot, Path.GetDirectoryName(e.OriginalPath)!, create: true, leases);
                _journal.Update(e, EntryStatus.Restoring);
                ShellRecycle.Restore(item);
                for (int i = 0; i < 100 && !File.Exists(e.OriginalPath); i++) Thread.Sleep(100);   // the shell restores in the background
                if (!File.Exists(e.OriginalPath))
                    return new PutBackResult(false, "Windows didn't put it back. You can restore it from the Recycle Bin yourself.", e);
            }

            RestoreAttributes(e.OriginalPath, e.Attributes);

            // Verify it came back as the same photo.
            string? mismatch = null;
            try
            {
                if (CloudAttributes.IsOnlineOnly(_fs.GetAttributes(e.OriginalPath)))
                    throw new IOException("it is online-only now, so PhotoTwins didn't read it");
                using var open = _fs.OpenRead(e.OriginalPath);
                if (open.Info.Size != e.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(open.Stream)), e.Sha256, StringComparison.OrdinalIgnoreCase))
                    mismatch = "It was put back, but its contents don't match what was removed. Check the photo.";
            }
            catch (Exception ex) { mismatch = "It was put back, but PhotoTwins couldn't check it: " + ex.Message; }

            leases.Dispose(); // empty holding folders can only be tidied after their leases are released
            if (e.Method == RemoveMethod.Folder && e.Destination != null) TidyEmptyHoldingFolders(e.Destination);

            JournalEntry restored;
            try { restored = _journal.Update(e, EntryStatus.Restored, null, mismatch); }
            catch (Exception ex)
            {
                _log.Error($"The photo {e.OriginalPath} was put back but its record couldn't be finished; it will be checked at the next start-up", ex);
                restored = e with { Status = EntryStatus.Restored, Reason = mismatch };
                return new PutBackResult(true, (mismatch == null ? "Put back." : mismatch) + " The record couldn't be finished; it will be checked next time. Details were saved to the log.", restored);
            }
            _log.Info($"Put back {e.OriginalPath}" + (mismatch != null ? " (" + mismatch + ")" : ""));
            return new PutBackResult(true, mismatch ?? "Put back.", restored);
        }
        catch (Exception ex)
        {
            _log.Error($"Couldn't put back {e.OriginalPath}", ex);
            return new PutBackResult(false, "Couldn't put it back: " + ex.Message, e);
        }
    }

    // ------------------------------------------------------------------------------------------ recovery after an interruption

    /// <summary>
    /// If the app or the PC stopped in the middle of a removal, some journal entries are still "starting". Work out what really
    /// happened to each photo and fix the record, before anything else happens. Returns a plain note for each thing worth telling the user.
    /// </summary>
    public List<string> Reconcile()
    {
        var notes = new List<string>();
        int finished = 0, untouched = 0, unsure = 0, restoredCount = 0, retryUndo = 0, unsureUndo = 0;
        BinSnapshot? snapshot = null;   // the Recycle Bin is read once, and only if a record needs it
        foreach (var e in _journal.Entries().Where(x => x.Status is EntryStatus.Starting or EntryStatus.Restoring))
        {
            try
            {
                if (e.Status == EntryStatus.Restoring)
                {
                    var (root, realRoot) = RestoreAnchor(e);
                    bool original = File.Exists(e.OriginalPath);
                    if (original) EnsureTrustedDirectory(root, realRoot, Path.GetDirectoryName(e.OriginalPath)!, create: false);
                    bool sourceExists, sourceMatches;
                    if (e.Method == RemoveMethod.Folder)
                    {
                        sourceExists = e.Destination != null && File.Exists(e.Destination);
                        sourceMatches = false;
                        if (sourceExists)
                        {
                            EnsureTrustedDirectory(root, realRoot, Path.GetDirectoryName(e.Destination!)!, create: false);
                            if (_fs.IsLink(e.Destination!) || !SamePath(_fs.GetRealPath(e.Destination!), Path.Combine(realRoot, Path.GetRelativePath(root, e.Destination!))))
                                throw new IOException("The held photo no longer leads to its recorded location.");
                            if (!CloudAttributes.IsOnlineOnly(_fs.GetAttributes(e.Destination!)))
                            {
                                using var held = _fs.OpenRead(e.Destination!);
                                sourceMatches = held.Info.Size == e.Size && string.Equals(Convert.ToHexString(SHA256.HashData(held.Stream)), e.Sha256, StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    }
                    else
                    {
                        snapshot ??= ShellRecycle.Snapshot();
                        var items = snapshot.Find(e.OriginalPath);
                        sourceExists = items.Count > 0;
                        sourceMatches = FindMatching(items, e) != null;
                    }
                    bool matchesOriginal = false;
                    if (original && !sourceExists && !_fs.IsLink(e.OriginalPath)
                        && SamePath(_fs.GetRealPath(e.OriginalPath), Path.Combine(realRoot, Path.GetRelativePath(root, e.OriginalPath)))
                        && !CloudAttributes.IsOnlineOnly(_fs.GetAttributes(e.OriginalPath)))
                    {
                        using var opened = _fs.OpenRead(e.OriginalPath);
                        matchesOriginal = opened.Info.Size == e.Size && string.Equals(Convert.ToHexString(SHA256.HashData(opened.Stream)), e.Sha256, StringComparison.OrdinalIgnoreCase);
                    }
                    if (matchesOriginal)
                    { _journal.Update(e, EntryStatus.Restored); restoredCount++; }
                    else if (!original && sourceMatches) { _journal.Update(e, EntryStatus.Done); retryUndo++; }
                    else { _journal.Update(e, EntryStatus.Unknown, null, "Interrupted while putting it back: check both locations."); unsureUndo++; }
                    continue;
                }
                bool originalThere = File.Exists(e.OriginalPath);
                if (e.Method == RemoveMethod.Folder)
                {
                    bool destThere = e.Destination != null && File.Exists(e.Destination);
                    if (!originalThere && destThere) { _journal.Update(e, EntryStatus.Done, e.Destination); finished++; }
                    else if (originalThere && !destThere) { _journal.Update(e, EntryStatus.Failed, null, "Interrupted before the photo was moved."); untouched++; }
                    else { _journal.Update(e, EntryStatus.Unknown, null, "Interrupted: couldn't tell where the photo is."); unsure++; }
                }
                else
                {
                    if (originalThere) { _journal.Update(e, EntryStatus.Failed, null, "Interrupted before the photo was recycled."); untouched++; }
                    else
                    {
                        snapshot ??= ShellRecycle.Snapshot();
                        var item = FindMatching(snapshot.Find(e.OriginalPath), e);
                        if (item != null) { _journal.Update(e, EntryStatus.Done, item.BinPath); finished++; }
                        else { _journal.Update(e, EntryStatus.Unknown, null, "Interrupted: the photo is gone from its folder and isn't in the Recycle Bin."); unsure++; }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Couldn't recover a removal record for " + e.OriginalPath, ex);
                if (e.Status == EntryStatus.Restoring)
                {
                    try { _journal.Update(e, EntryStatus.Unknown, null, "Interrupted while putting it back: the locations couldn't be verified."); } catch { }
                    unsureUndo++;
                }
                else unsure++;
            }
        }
        if (finished + untouched + unsure > 0)
        {
            var parts = new List<string>();
            if (finished > 0) parts.Add($"{finished} had been removed (they're in the Undo list)");
            if (untouched > 0) parts.Add($"{untouched} hadn't been touched");
            if (unsure > 0) parts.Add($"{unsure} couldn't be checked: look in the Recycle Bin and the \"Duplicates to delete\" folders");
            notes.Add("PhotoTwins was stopped in the middle of removing photos last time. " + string.Join("; ", parts) + ".");
            _log.Warn(notes[^1]);
        }
        if (restoredCount + retryUndo + unsureUndo > 0)
        {
            var parts = new List<string>();
            if (restoredCount > 0) parts.Add($"{restoredCount} had been put back");
            if (retryUndo > 0) parts.Add($"{retryUndo} can still be put back from Undo");
            if (unsureUndo > 0) parts.Add($"{unsureUndo} couldn't be checked: look in both locations");
            notes.Add("PhotoTwins was stopped while putting photos back last time. " + string.Join("; ", parts) + ".");
            _log.Warn(notes[^1]);
        }
        return notes;
    }
}
