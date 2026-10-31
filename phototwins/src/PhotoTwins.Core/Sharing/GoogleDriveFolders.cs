using Microsoft.Data.Sqlite;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Native;

namespace PhotoTwins.Core.Sharing;

/// <summary>
/// Finds the folders on THIS PC that Google Drive for desktop keeps in sync: the folders it backs up (they appear online under
/// "Other computers") and a mirrored "My Drive" folder. Deleting a photo from one of them also removes it from Google Drive and
/// from Pete's other devices, so they get the same one-line warning as OneDrive. (The streamed Google Drive letter is handled
/// separately: it is never scanned at all.) Google keeps its folder list in a small database in the user's own app data; it is
/// opened read-only and never changed. The database stores each folder by Windows file ID, which is turned back into a path.
/// </summary>
public static class GoogleDriveFolders
{
    /// <summary>The default place Google Drive for desktop keeps its data for this user.</summary>
    public static string DefaultDriveFsRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "DriveFS");

    public static List<CloudRoot> Read(string driveFsRoot, AppLog? log = null)
    {
        var roots = new List<CloudRoot>();
        try
        {
            if (!Directory.Exists(driveFsRoot)) return roots;
            foreach (var account in Directory.EnumerateDirectories(driveFsRoot))
            {
                var db = Path.Combine(account, "mirror_sqlite.db");
                if (!File.Exists(db)) continue;
                try { roots.AddRange(ReadOne(db)); }
                catch (Exception ex) { log?.Warn($"Couldn't read Google Drive's folder list ({db}): {ex.Message}"); }
            }
        }
        catch (Exception ex) { log?.Warn("Couldn't look for Google Drive folders: " + ex.Message); }
        return roots;
    }

    private static IEnumerable<CloudRoot> ReadOne(string db)
    {
        var found = new List<CloudRoot>();
        var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // every folder Google Drive treats as a root on this PC: backed-up folders and a mirrored My Drive
        cmd.CommandText = "SELECT m.volume, m.inode FROM root_config r JOIN mirror_item m ON m.local_stable_id = r.local_stable_id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var volume = r.GetString(0).Trim('{', '}');
            var fileId = r.GetInt64(1);
            try
            {
                var path = FinalPath.OfFileId(volume, fileId);
                if (!string.IsNullOrWhiteSpace(path)) found.Add(new CloudRoot(path, "Google Drive"));
            }
            catch { /* a folder that no longer exists on this PC has nothing to protect */ }
        }
        return found;
    }
}
