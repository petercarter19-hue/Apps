using System.Text.Json;
using PhotoTwins.Core.Logging;

namespace PhotoTwins.Core.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    /// <summary>The folders Pete picked to scan, remembered between runs.</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>True once the starting folder (Pictures) has been offered, so removing it later sticks.</summary>
    public bool FoldersInitialized { get; set; }
    /// <summary>Folders Pete marked as shared with other people (in case Windows can't see the sharing itself).</summary>
    public List<string> SharedFolders { get; set; } = new();
    /// <summary>Look-alikes (different photos that look alike, like burst shots) are off until Pete turns them on.</summary>
    public bool LookAlikesOn { get; set; }
    /// <summary>"Strict", "Normal" or "Loose".</summary>
    public string LookAlikeStrictness { get; set; } = "Normal";
    /// <summary>Keeper choices Pete made: group name (made from the photos' contents) to the path of the photo he starred.</summary>
    public Dictionary<string, string> KeeperPicks { get; set; } = new();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly AppLog _log;

    /// <summary>Plain-English note if the saved settings couldn't be read, shown on screen (never silent).</summary>
    public string? LoadProblem { get; private set; }

    public SettingsStore(AppPaths paths, AppLog log) { _paths = paths; _log = log; }

    public AppSettings Load()
    {
        var file = _paths.SettingsFile;
        if (!File.Exists(file)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new AppSettings();
            s.Folders ??= new();
            s.SharedFolders ??= new();
            s.KeeperPicks ??= new();
            return s;
        }
        catch (Exception ex)
        {
            // Keep the damaged file so nothing is lost, and start fresh.
            try { File.Copy(file, file + ".damaged", overwrite: true); } catch { /* best effort */ }
            LoadProblem = "Your saved settings couldn't be read, so PhotoTwins started fresh. The old file was kept as settings.json.damaged.";
            _log.Error("Settings file unreadable: " + file, ex);
            return new AppSettings();
        }
    }

    /// <summary>Plain-English note if saving the settings failed (shown on screen, also logged).</summary>
    public string? SaveProblem { get; private set; }

    /// <summary>Saves the settings. Returns false (and says why in <see cref="SaveProblem"/>) instead of throwing.</summary>
    public bool TrySave(AppSettings s)
    {
        try
        {
            AtomicFile.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(s, Json));
            SaveProblem = null;
            return true;
        }
        catch (Exception ex)
        {
            SaveProblem = "PhotoTwins couldn't save your settings (" + ex.Message + "). Your folder list will be forgotten when you close it.";
            _log.Error("Settings could not be saved: " + _paths.SettingsFile, ex);
            return false;
        }
    }
}
