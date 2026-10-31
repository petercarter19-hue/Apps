using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Glance;

internal sealed class Settings
{
    public bool ShowCpuTile { get; set; } // off by default: one number by the clock; the CPU tile is in the right-click menu

    /// <summary>True the first time Glance runs on this PC (no settings file yet).</summary>
    [JsonIgnore] public bool FirstRun { get; private set; }

    public static string Dir => Instance.StateDir;
    private static string FilePath => Path.Combine(Dir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string RunValue => Instance.EntryName;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            var fresh = new Settings { FirstRun = true };
            fresh.Save();
            return fresh;
        }
        catch (Exception ex) { Log.Write(ex); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    // "Start with Windows" uses the per-user Run key, so it shows up in Task Manager's Startup tab like any other app.
    //
    // A test copy (GLANCE_TEST_NS) must never register itself at sign-in, so it keeps its "startup entry" in a file in its own
    // settings folder instead of the real Run key. If a test run is ever killed half-way, nothing is left to start at sign-in.
    private static string StartupFile => Path.Combine(Dir, "startup-entry.txt");

    public static bool StartsWithWindows()
    {
        if (Instance.IsTest)
        {
            try { return File.Exists(StartupFile) && File.ReadAllText(StartupFile).Length > 0; }
            catch { return false; }
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void SetStartWithWindows(bool on)
    {
        if (Instance.IsTest)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (on) File.WriteAllText(StartupFile, $"\"{Environment.ProcessPath}\"");
                else File.Delete(StartupFile);
            }
            catch (Exception ex) { Log.Write(ex); }
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, false);
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    public static bool IsTempCopy =>
        Environment.ProcessPath?.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>If Start with Windows is on but points at an old copy (the file was moved), point it at this one.</summary>
    public static void RefreshStartupPath()
    {
        if (Instance.IsTest)
        {
            try
            {
                string want = $"\"{Environment.ProcessPath}\"";
                if (File.Exists(StartupFile) && File.ReadAllText(StartupFile) is { Length: > 0 } s && s != want) File.WriteAllText(StartupFile, want);
            }
            catch (Exception ex) { Log.Write(ex); }
            return;
        }
        try
        {
            string want = $"\"{Environment.ProcessPath}\"";
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) is string s && s.Length > 0 && s != want) key.SetValue(RunValue, want);
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    /// <summary>The only files Glance ever writes in its settings folder.</summary>
    internal static readonly string[] OwnFiles = { "settings.json", "log.txt", "preview.txt", "card.png", "tiles.png", "startup-entry.txt" };

    /// <summary>
    /// "Remove Glance from this PC": no startup entry, no Start menu shortcut, and none of Glance's own files (settings, log, preview pictures).
    /// It deletes those named files only, never "everything in the folder", and removes the folder only if that leaves it empty.
    /// Documents, photos and anything else are never touched.
    /// </summary>
    public static void RemoveAll()
    {
        SetStartWithWindows(false);
        Shortcut.Remove();
        foreach (var name in OwnFiles)
        {
            try { File.Delete(Path.Combine(Dir, name)); }
            catch { }
        }
        try { if (Directory.Exists(Dir) && !Directory.EnumerateFileSystemEntries(Dir).Any()) Directory.Delete(Dir); }
        catch { }
    }
}
