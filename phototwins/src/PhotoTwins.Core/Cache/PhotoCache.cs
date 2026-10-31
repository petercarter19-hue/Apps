using Microsoft.Data.Sqlite;
using PhotoTwins.Core.Imaging;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Native;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins.Core.Cache;

/// <summary>What the cache remembers about one photo file.</summary>
/// <param name="Analysis">The fingerprint and hidden info, or null for a photo remembered by an older version that only knew its SHA-256 (it gets read again).</param>
public sealed record CachedPhoto(string Path, FileIdentity Identity, long Size, long LastWriteUtcTicks, byte[] Sha256, Analysis? Analysis = null);

/// <summary>
/// Remembers each photo's "content fingerprint" (SHA-256) so the next scan only reads new or changed files.
/// Saved in small batches in a crash-safe database, so a scan that is stopped or interrupted loses almost nothing.
/// </summary>
public class PhotoCache : IDisposable
{
    private const int SchemaVersion = 3;
    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private readonly AppLog _log;

    public PhotoCache(string dbPath, AppLog log)
    {
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        _db.Open();
        try { Init(); }
        catch (SqliteException ex)
        {
            // A damaged cache is only a speed-up: set it aside, say so in the log, and start a new one.
            _log.Error("Cache file unreadable; starting a new one: " + dbPath, ex);
            _db.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Move(dbPath, dbPath + ".damaged", overwrite: true); } catch { /* best effort */ }
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
            _db.Open();
            Init();
            DamagedAndReset = true;
        }
    }

    /// <summary>True if the cache had to be thrown away this run (so the first scan is slower than usual).</summary>
    public bool DamagedAndReset { get; }

    private void Init()
    {
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        long version = Scalar("PRAGMA user_version;");
        if (version > SchemaVersion) throw new SqliteException("The cache was made by a newer version of PhotoTwins.", 1);
        // Version 2 columns: the fingerprint (ph, dh, tiny, w, h), the hidden photo info (taken ... gps), and err (why the picture
        // couldn't be opened). analyzed = 0 means "only the SHA-256 is known".
        const string V2Columns = "analyzed INTEGER NOT NULL DEFAULT 0, ph INTEGER, dh INTEGER, tiny BLOB, w INTEGER, h INTEGER, " +
                                 "taken INTEGER, subsec TEXT, make TEXT, model TEXT, serial TEXT, uid TEXT, gps INTEGER, err TEXT";
        Exec(@"CREATE TABLE IF NOT EXISTS files(
                 path TEXT PRIMARY KEY COLLATE NOCASE,
                 vol INTEGER NOT NULL, fileid INTEGER NOT NULL,
                 size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                 sha BLOB NOT NULL, seen INTEGER NOT NULL, " + V2Columns + ");");
        if (version == 1)
        {
            // Made by step 1, which remembered only each photo's SHA-256: add the new columns empty, so those photos are read once more.
            foreach (var col in V2Columns.Split(", "))
                Exec("ALTER TABLE files ADD COLUMN " + col);
        }
        if (version == 2)
            // Older readers could discard XMP fractions or attach an unrelated EXIF fraction. Re-read all dated photos once.
            Exec("UPDATE files SET analyzed=0 WHERE taken IS NOT NULL;");
        Exec("CREATE INDEX IF NOT EXISTS files_identity ON files(vol, fileid);");
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        Exec($"PRAGMA user_version={SchemaVersion};");
    }

    private void Exec(string sql, Action<SqliteCommand>? bind = null)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        bind?.Invoke(c);
        c.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar() ?? 0L);
    }

    private string? GetMeta(string key)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT value FROM meta WHERE key=@k";
        c.Parameters.AddWithValue("@k", key);
        return c.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=@v",
            c => { c.Parameters.AddWithValue("@k", key); c.Parameters.AddWithValue("@v", value); });

    /// <summary>True when the last scan began but never finished (stopped, closed, crashed). The next scan simply picks up from the cache.</summary>
    public bool LastScanUnfinished { get { lock (_gate) return GetMeta("scan_state") == "running"; } }

    public long BeginScan()
    {
        lock (_gate)
        {
            long id = long.Parse(GetMeta("scan_id") ?? "0") + 1;
            SetMeta("scan_id", id.ToString());
            SetMeta("scan_state", "running");
            return id;
        }
    }

    public void CompleteScan()
    {
        lock (_gate) SetMeta("scan_state", "complete");
    }

    public int RowCount { get { lock (_gate) return (int)Scalar("SELECT COUNT(*) FROM files"); } }

    public Dictionary<string, CachedPhoto> LoadAll()
    {
        lock (_gate)
        {
            var map = new Dictionary<string, CachedPhoto>(StringComparer.OrdinalIgnoreCase);
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT path,vol,fileid,size,mtime,sha,analyzed,ph,dh,tiny,w,h,taken,subsec,make,model,serial,uid,gps,err FROM files";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var p = r.GetString(0);
                Analysis? analysis = null;
                if (r.GetInt64(6) != 0)
                {
                    var info = new PhotoInfo(Nullable(r, 12), Text(r, 13), Text(r, 14), Text(r, 15), Text(r, 16), Text(r, 17), !r.IsDBNull(18) && r.GetInt64(18) != 0);
                    var err = Text(r, 19);
                    analysis = err != null || r.IsDBNull(7)
                        ? new Analysis(null, info, err ?? "It couldn't be opened as a picture.")
                        : new Analysis(new Fingerprint(unchecked((ulong)r.GetInt64(7)), unchecked((ulong)r.GetInt64(8)), (byte[])r.GetValue(9), (int)r.GetInt64(10), (int)r.GetInt64(11)), info, null);
                }
                map[p] = new CachedPhoto(p, new FileIdentity((uint)r.GetInt64(1), unchecked((ulong)r.GetInt64(2))),
                    r.GetInt64(3), r.GetInt64(4), (byte[])r.GetValue(5), analysis);
            }
            return map;
        }
    }

    private static long? Nullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public virtual void Upsert(IReadOnlyCollection<CachedPhoto> rows, long scanId)
    {
        if (rows.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = @"INSERT INTO files(path,vol,fileid,size,mtime,sha,seen,analyzed,ph,dh,tiny,w,h,taken,subsec,make,model,serial,uid,gps,err)
                              VALUES(@p,@v,@f,@s,@m,@h,@n,@a,@ph,@dh,@t,@w,@ht,@tk,@ss,@mk,@md,@sn,@ui,@gp,@er)
                              ON CONFLICT(path) DO UPDATE SET vol=@v,fileid=@f,size=@s,mtime=@m,sha=@h,seen=@n,analyzed=@a,ph=@ph,dh=@dh,tiny=@t,w=@w,h=@ht,
                                taken=@tk,subsec=@ss,make=@mk,model=@md,serial=@sn,uid=@ui,gps=@gp,err=@er";
            SqliteParameter P(string name, SqliteType t) => c.Parameters.Add(name, t);
            var p = P("@p", SqliteType.Text); var v = P("@v", SqliteType.Integer); var f = P("@f", SqliteType.Integer);
            var s = P("@s", SqliteType.Integer); var m = P("@m", SqliteType.Integer); var h = P("@h", SqliteType.Blob); var n = P("@n", SqliteType.Integer);
            var a = P("@a", SqliteType.Integer); var ph = P("@ph", SqliteType.Integer); var dh = P("@dh", SqliteType.Integer); var t = P("@t", SqliteType.Blob);
            var w = P("@w", SqliteType.Integer); var ht = P("@ht", SqliteType.Integer); var tk = P("@tk", SqliteType.Integer); var ss = P("@ss", SqliteType.Text);
            var mk = P("@mk", SqliteType.Text); var md = P("@md", SqliteType.Text); var sn = P("@sn", SqliteType.Text); var ui = P("@ui", SqliteType.Text);
            var gp = P("@gp", SqliteType.Integer); var er = P("@er", SqliteType.Text);
            foreach (var row in rows)
            {
                p.Value = row.Path; v.Value = (long)row.Identity.VolumeSerial; f.Value = unchecked((long)row.Identity.FileId);
                s.Value = row.Size; m.Value = row.LastWriteUtcTicks; h.Value = row.Sha256; n.Value = scanId;
                var an = row.Analysis;
                a.Value = an == null ? 0L : 1L;
                ph.Value = an?.Fingerprint is { } fp ? unchecked((long)fp.PHash) : DBNull.Value;
                dh.Value = an?.Fingerprint is { } fq ? unchecked((long)fq.DHash) : DBNull.Value;
                t.Value = (object?)an?.Fingerprint?.Tiny ?? DBNull.Value;
                w.Value = an?.Fingerprint is { } fw ? (long)fw.Width : DBNull.Value;
                ht.Value = an?.Fingerprint is { } fh ? (long)fh.Height : DBNull.Value;
                tk.Value = an?.Info.TakenTicks is { } tt ? tt : DBNull.Value;
                ss.Value = (object?)an?.Info.SubSec ?? DBNull.Value;
                mk.Value = (object?)an?.Info.Make ?? DBNull.Value;
                md.Value = (object?)an?.Info.Model ?? DBNull.Value;
                sn.Value = (object?)an?.Info.Serial ?? DBNull.Value;
                ui.Value = (object?)an?.Info.UniqueId ?? DBNull.Value;
                gp.Value = an == null ? DBNull.Value : (an.Info.HasGps ? 1L : 0L);
                er.Value = (object?)an?.Error ?? DBNull.Value;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Forgets photos that were removed, so the memory never claims a file that is no longer there.</summary>
    public virtual void RemovePaths(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var d = _db.CreateCommand();
            d.Transaction = tx;
            d.CommandText = "DELETE FROM files WHERE path=@p";
            var p = d.Parameters.Add("@p", SqliteType.Text);
            foreach (var path in paths) { p.Value = path; d.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public virtual void MarkSeen(IReadOnlyCollection<string> paths, long scanId)
    {
        if (paths.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "UPDATE files SET seen=@n WHERE path=@p";
            var n = c.Parameters.Add("@n", SqliteType.Integer);
            var p = c.Parameters.Add("@p", SqliteType.Text);
            n.Value = scanId;
            foreach (var path in paths) { p.Value = path; c.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    /// <summary>After a complete scan: forget photos under the scanned folders that weren't seen this time (deleted, moved away).</summary>
    public int RemoveUnseenUnder(IReadOnlyList<string> roots, long scanId)
    {
        lock (_gate)
        {
            var stale = new List<string>();
            using (var c = _db.CreateCommand())
            {
                c.CommandText = "SELECT path FROM files WHERE seen < @n";
                c.Parameters.AddWithValue("@n", scanId);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var path = r.GetString(0);
                    if (roots.Any(root => ScanPolicy.IsSameOrInside(path, root))) stale.Add(path);
                }
            }
            if (stale.Count == 0) return 0;
            using var tx = _db.BeginTransaction();
            using var d = _db.CreateCommand();
            d.Transaction = tx;
            d.CommandText = "DELETE FROM files WHERE path=@p";
            var p = d.Parameters.Add("@p", SqliteType.Text);
            foreach (var s in stale) { p.Value = s; d.ExecuteNonQuery(); }
            tx.Commit();
            return stale.Count;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _db.Dispose(); } catch { /* closing */ }
        }
    }
}
