using System.Text.RegularExpressions;

namespace Spotlight;

/// <summary>
/// The names that tell Windows which copy of Spotlight this is: the single-copy lock, the quit signal, the settings folder
/// and the start-at-sign-in entry. Normally they are Spotlight's fixed names. For tests only, the environment variable
/// <c>SPOTLIGHT_TEST_NS</c> (1 to 24 letters or digits) gives a copy its own set of all of them, so a test copy can never see,
/// replace or quit the real Spotlight, or read or write the real settings or sign-in entry.
/// <c>SPOTLIGHT_TEST_DIR</c> (a full path, only with the NS) moves that copy's settings folder.
/// </summary>
internal static class Instance
{
    private static readonly Regex NsPattern = new("^[A-Za-z0-9]{1,24}$", RegexOptions.Compiled);

    public static string? TestNs { get; } = Read("SPOTLIGHT_TEST_NS");
    public static bool IsTest => TestNs != null;
    private static string? TestDir { get; } = Read("SPOTLIGHT_TEST_DIR");

    /// <summary>False when the test variables are set but unusable; the app then exits instead of guessing.</summary>
    public static bool EnvironmentIsUsable(out string problem)
    {
        problem = "";
        string? ns = Environment.GetEnvironmentVariable("SPOTLIGHT_TEST_NS");
        string? dir = Environment.GetEnvironmentVariable("SPOTLIGHT_TEST_DIR");
        if (!string.IsNullOrEmpty(ns) && !NsPattern.IsMatch(ns)) { problem = "SPOTLIGHT_TEST_NS must be 1 to 24 letters or digits."; return false; }
        if (!string.IsNullOrEmpty(dir))
        {
            if (string.IsNullOrEmpty(ns)) { problem = "SPOTLIGHT_TEST_DIR needs SPOTLIGHT_TEST_NS."; return false; }
            if (!Path.IsPathFullyQualified(dir)) { problem = "SPOTLIGHT_TEST_DIR must be a full path."; return false; }
        }
        return true;
    }

    private static string? Read(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string Suffix => TestNs == null ? "" : "." + TestNs;

    public static string MutexName => "Spotlight.SingleInstance.v1" + Suffix;
    public static string QuitEventName => "Spotlight.Quit.v1" + Suffix;
    /// <summary>Held while the settings window is open, so only one opens. The installer also waits for it.</summary>
    public static string SettingsMutexName => "Spotlight.Settings.v1" + Suffix;
    /// <summary>Held while settings.json is read, changed and saved, so two copies of Spotlight never overwrite each other's edit.</summary>
    /// <summary>Held by the area-picker sheet for as long as it is on screen, so the installer can tell it is still running.</summary>
    public static string PickerMutexName => "Spotlight.Picker.v1" + Suffix;
    public static string SettingsLockName => "Spotlight.SettingsFile.v1" + Suffix;
    /// <summary>Set by the settings window after it saves, so the tray copy re-reads settings.json at once.</summary>
    public static string ReloadEventName => "Spotlight.Reload.v1" + Suffix;
    public static string SettingsTitle => TestNs == null ? "Spotlight settings" : $"Spotlight settings [{TestNs}]";
    public static string TrayTitle => TestNs == null ? "Spotlight tray" : $"Spotlight tray [{TestNs}]";
    public static string EntryName => TestNs == null ? "Spotlight" : "Spotlight-test-" + TestNs;

    public static string StateDir => TestNs == null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight")
        : TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight-test-" + TestNs);
}
