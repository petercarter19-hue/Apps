namespace PhotoTwins.Core.Settings;

/// <summary>Where the app keeps its own data. Never inside a photo folder and never in the code repository.</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string CacheFile => Path.Combine(DataDir, "cache.db");
    public string ThumbsFile => Path.Combine(DataDir, "pictures.db");
    public string LogDir => Path.Combine(DataDir, "logs");

    public AppPaths(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
    }

    public static AppPaths ForThisUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTwins"));
}
