using Microsoft.Data.Sqlite;
using PhotoTwins.Core.Logging;

namespace PhotoTwins.Core.Cache;

/// <summary>
/// Remembers a small picture of each photo that is shown in a group, so the pictures appear at once the next time (and the first time,
/// because the scan makes them while the photo is already open). Each is a small JPEG (about 450 pixels along the long side) kept under the
/// photo's CONTENT key (its size and SHA-256), so copies share one, a renamed or moved photo keeps its picture, and a photo that changed
/// gets a new one. It is only a speed-up: it can be deleted at any time and is rebuilt as needed. It is never used to decide anything.
/// </summary>
public sealed class ThumbnailStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private readonly AppLog _log;
    private readonly string _path;

    public ThumbnailStore(string dbPath, AppLog log)
    {
        _log = log; _path = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _db = Open(dbPath);
        try { Init(); }
        catch (SqliteException ex)
        {
            _log.Error("Picture memory unreadable; starting a new one: " + dbPath, ex);
            _db.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Move(dbPath, dbPath + ".damaged", overwrite: true); } catch { /* best effort */ }
            _db = Open(dbPath);
            Init();
        }
    }

    private static SqliteConnection Open(string dbPath)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private void Init()
    {
        Exec("PRAGMA auto_vacuum=INCREMENTAL;");   // takes effect for a new file: space freed by pruning is given back to the disk
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("CREATE TABLE IF NOT EXISTS thumbs(key TEXT PRIMARY KEY, added INTEGER NOT NULL, jpeg BLOB NOT NULL);");
        Exec("CREATE INDEX IF NOT EXISTS thumbs_added ON thumbs(added);");
    }

    private void Exec(string sql, Action<SqliteCommand>? bind = null)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        bind?.Invoke(c);
        c.ExecuteNonQuery();
    }

    /// <summary>The key a photo's picture is kept under: its size and SHA-256.</summary>
    public static string KeyOf(long size, byte[] sha256) => $"{size}-{Convert.ToHexString(sha256)}";

    public byte[]? TryGet(string key)
    {
        try
        {
            lock (_gate)
            {
                using var c = _db.CreateCommand();
                c.CommandText = "SELECT jpeg FROM thumbs WHERE key=@k";
                c.Parameters.AddWithValue("@k", key);
                return c.ExecuteScalar() as byte[];
            }
        }
        catch (Exception ex) { _log.Warn("Couldn't read a remembered picture: " + ex.Message); return null; }
    }

    public bool Has(string key)
    {
        try
        {
            lock (_gate)
            {
                using var c = _db.CreateCommand();
                c.CommandText = "SELECT 1 FROM thumbs WHERE key=@k";
                c.Parameters.AddWithValue("@k", key);
                return c.ExecuteScalar() != null;
            }
        }
        catch { return false; }
    }

    /// <summary>Remembers a picture. Never throws: a picture that can't be saved is simply drawn again next time.</summary>
    public void Put(string key, byte[] jpeg)
    {
        try
        {
            lock (_gate)
                Exec("INSERT OR REPLACE INTO thumbs(key, added, jpeg) VALUES(@k, @a, @j)", c =>
                {
                    c.Parameters.AddWithValue("@k", key);
                    c.Parameters.AddWithValue("@a", DateTime.UtcNow.Ticks);
                    c.Parameters.AddWithValue("@j", jpeg);
                });
        }
        catch (Exception ex) { _log.Warn("Couldn't remember a picture: " + ex.Message); }
    }

    /// <summary>Forgets these pictures (photos that are in no group any more).</summary>
    public int Remove(IEnumerable<string> keys)
    {
        int n = 0;
        try
        {
            lock (_gate)
            {
                using var tx = _db.BeginTransaction();
                using var d = _db.CreateCommand();
                d.Transaction = tx;
                d.CommandText = "DELETE FROM thumbs WHERE key=@k";
                var p = d.Parameters.Add("@k", SqliteType.Text);
                foreach (var k in keys) { p.Value = k; n += d.ExecuteNonQuery(); }
                tx.Commit();
                if (n > 0) Exec("PRAGMA incremental_vacuum;");
            }
        }
        catch (Exception ex) { _log.Warn("Couldn't tidy the remembered pictures: " + ex.Message); }
        return n;
    }

    /// <summary>Keeps the memory sensible: forgets pictures not made for a long time, and the oldest ones if it grows past the limit.</summary>
    public int TrimOld(int maxAgeDays = 120, long maxBytes = 400L * 1024 * 1024)
    {
        int n = 0;
        try
        {
            lock (_gate)
            {
                var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays).Ticks;
                using (var c = _db.CreateCommand()) { c.CommandText = "DELETE FROM thumbs WHERE added < @a"; c.Parameters.AddWithValue("@a", cutoff); n += c.ExecuteNonQuery(); }
                long total;
                using (var c = _db.CreateCommand()) { c.CommandText = "SELECT COALESCE(SUM(LENGTH(jpeg)),0) FROM thumbs"; total = Convert.ToInt64(c.ExecuteScalar()); }
                if (total > maxBytes)
                {
                    long target = maxBytes * 3 / 4;
                    var victims = new List<string>();
                    using (var c = _db.CreateCommand())
                    {
                        c.CommandText = "SELECT key, LENGTH(jpeg) FROM thumbs ORDER BY added";
                        using var r = c.ExecuteReader();
                        while (total > target && r.Read()) { victims.Add(r.GetString(0)); total -= r.GetInt64(1); }
                    }
                    foreach (var k in victims) Exec("DELETE FROM thumbs WHERE key=@k", c => c.Parameters.AddWithValue("@k", k));
                    n += victims.Count;
                }
                if (n > 0) Exec("PRAGMA incremental_vacuum;");
            }
        }
        catch (Exception ex) { _log.Warn("Couldn't trim the remembered pictures: " + ex.Message); }
        return n;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var c = _db.CreateCommand();
                c.CommandText = "SELECT COUNT(*) FROM thumbs";
                return Convert.ToInt32(c.ExecuteScalar());
            }
        }
    }

    /// <summary>How much disk the remembered pictures use (the database and its journal).</summary>
    public long DiskBytes
    {
        get
        {
            long total = 0;
            foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) total += new FileInfo(f).Length; } catch { /* best effort */ }
            return total;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { /* closing */ }
            try { _db.Dispose(); } catch { /* closing */ }
        }
    }
}
