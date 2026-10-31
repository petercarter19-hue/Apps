using System.Text;
using System.Text.Json;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Native;

namespace PhotoTwins.Core.Removal;

public static class EntryStatus
{
    public const string Starting = "starting";   // written BEFORE anything is touched
    public const string Done = "done";           // the photo was moved away and can be put back
    public const string Restoring = "restoring"; // flushed before Undo moves anything
    public const string Failed = "failed";       // nothing was moved
    public const string Restored = "restored";   // put back by Undo
    public const string Gone = "gone";           // no longer where it was put; can't be put back
    public const string Unknown = "unknown";     // an interrupted removal that couldn't be confirmed either way
}

public static class RemoveMethod
{
    public const string Recycle = "recycle";
    public const string Folder = "folder";
}

/// <summary>One photo that was (or was about to be) removed, with everything needed to put it back.</summary>
public sealed record JournalEntry(
    string Id, string ActionId, DateTime TimeLocal,
    string OriginalPath, long Size, long LastWriteUtcTicks, string Sha256,
    string GroupKey, string KeeperPath,
    string Method, string Status, string? Destination, string? Reason, int Attributes = -1,
    string? SelectedRoot = null, string? RealRoot = null, string? OriginalRealPath = null, FileIdentity? OriginalIdentity = null)
{
    public string Name => Path.GetFileName(OriginalPath);
    public string Folder => Path.GetDirectoryName(OriginalPath) ?? "";
}

/// <summary>
/// The record of every removal, kept on disk (one line per change, written and flushed to disk before anything is moved).
/// Undo is built on it, and so is recovery if the app or the PC stops in the middle of a removal.
/// </summary>
public class UndoJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly AppLog _log;

    /// <summary>Plain-English note if some saved lines couldn't be read (never silent).</summary>
    public string? LoadProblem { get; private set; }

    public UndoJournal(string path, AppLog log) { _path = path; _log = log; }

    public string FilePath => _path;

    public virtual void Append(JournalEntry e)
    {
        var line = JsonSerializer.Serialize(e, Json) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
            // If the last write was cut short (a power cut), the file doesn't end in a new line: start on a fresh line so the
            // broken one stays broken by itself and this one is whole.
            fs.Seek(0, SeekOrigin.End);
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                bool endsCleanly = fs.ReadByte() == '\n';
                fs.Seek(0, SeekOrigin.End);
                if (!endsCleanly) fs.WriteByte((byte)'\n');
            }
            var bytes = new UTF8Encoding(false).GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);   // on disk before the caller goes on
        }
    }

    /// <summary>Every entry in its latest state, in the order the photos were first recorded.</summary>
    public IReadOnlyList<JournalEntry> Entries()
    {
        lock (_gate)
        {
            var order = new List<string>();
            var latest = new Dictionary<string, JournalEntry>();
            int bad = 0;
            if (File.Exists(_path))
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<JournalEntry>(line, Json);
                        if (e == null) { bad++; continue; }
                        if (!latest.ContainsKey(e.Id)) order.Add(e.Id);
                        latest[e.Id] = e;
                    }
                    catch (JsonException) { bad++; }   // e.g. the last line cut short by a power cut
                }
            }
            if (bad > 0)
            {
                LoadProblem = $"{bad} line(s) of the removal record couldn't be read, so some removed photos may be missing from the Undo list.";
                _log.Error("Undo journal has unreadable lines: " + bad + " in " + _path);
            }
            return order.Select(id => latest[id]).ToList();
        }
    }

    public JournalEntry Update(JournalEntry e, string status, string? destination = null, string? reason = null, string? method = null)
    {
        var n = e with { Status = status, Destination = destination ?? e.Destination, Reason = reason, Method = method ?? e.Method };
        Append(n);
        return n;
    }
}
