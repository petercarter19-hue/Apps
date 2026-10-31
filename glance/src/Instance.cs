using System.Text.RegularExpressions;

namespace Glance;

/// <summary>
/// The names that tell Windows which copy of Glance this is: the single-copy lock, the quit signal, the settings folder,
/// the Start with Windows entry, the Start menu shortcut and the window titles.
///
/// Normally they are Glance's fixed names. For tests only, the environment variable <c>GLANCE_TEST_NS</c> (1 to 24 letters
/// or digits, made up by the test) gives a copy its own set of all of them. A test copy then can't see, replace or quit
/// the real Glance, and it never reads or writes the real settings, startup entry or shortcut.
/// <c>GLANCE_TEST_DIR</c> (a full path; only used together with <c>GLANCE_TEST_NS</c>) moves that copy's settings folder.
/// A bad value makes Glance refuse to start rather than quietly fall back to the real names.
/// </summary>
internal static class Instance
{
    private static readonly Regex NsPattern = new("^[A-Za-z0-9]{1,24}$", RegexOptions.Compiled);

    /// <summary>The test name, or null for the normal Glance.</summary>
    public static string? TestNs { get; } = Read("GLANCE_TEST_NS");
    public static bool IsTest => TestNs != null;

    private static string? TestDir { get; } = Read("GLANCE_TEST_DIR");

    public static bool IsValidNs(string? ns) => ns != null && NsPattern.IsMatch(ns);

    /// <summary>False when the test variables are set but unusable; the app then exits instead of guessing.</summary>
    public static bool EnvironmentIsUsable(out string problem) =>
        CheckEnvironment(Environment.GetEnvironmentVariable("GLANCE_TEST_NS"), Environment.GetEnvironmentVariable("GLANCE_TEST_DIR"), out problem);

    internal static bool CheckEnvironment(string? ns, string? dir, out string problem)
    {
        problem = "";
        if (!string.IsNullOrEmpty(ns) && !IsValidNs(ns)) { problem = "GLANCE_TEST_NS must be 1 to 24 letters or digits."; return false; }
        if (!string.IsNullOrEmpty(dir))
        {
            if (string.IsNullOrEmpty(ns)) { problem = "GLANCE_TEST_DIR needs GLANCE_TEST_NS."; return false; }
            if (!Path.IsPathFullyQualified(dir)) { problem = "GLANCE_TEST_DIR must be a full path."; return false; }
            if (OverlapsRealFolder(dir)) { problem = "GLANCE_TEST_DIR must not be, contain, or be inside the real Glance settings folder."; return false; }
        }
        return true;
    }

    /// <summary>The real settings folder, %LocalAppData%\Glance.</summary>
    internal static string RealStateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance");

    private static bool OverlapsRealFolder(string dir)
    {
        string a = Normalize(dir), b = Normalize(RealStateDir);
        return a == b || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return path.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    private static string? Read(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string Suffix => TestNs == null ? "" : "." + TestNs;

    public static string MutexName => "Glance.SingleInstance.v1" + Suffix;
    public static string QuitEventName => "Glance.Quit.v1" + Suffix;
    public static string CardTitle => TestNs == null ? "Glance" : $"Glance [{TestNs}]";
    public static string TrayTitle => TestNs == null ? "Glance tray" : $"Glance tray [{TestNs}]";

    /// <summary>Name of the Start with Windows value and of the Start menu shortcut.</summary>
    public static string EntryName => TestNs == null ? "Glance" : "Glance-test-" + TestNs;

    public static string StateDir => TestNs == null
        ? RealStateDir
        : TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance-test-" + TestNs);

    /// <summary>The Start menu's own folder for the normal copy; a test copy keeps its shortcut inside its own test folder.</summary>
    public static string ShortcutDir => TestNs == null
        ? Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        : Path.Combine(StateDir, "StartMenu");
}
