namespace Glance;

/// <summary>
/// What End needs to know about one process: its parent, when it started, which sign-in session it's in, its exe name,
/// and the account that owns it (null when Windows wouldn't say).
/// </summary>
internal readonly record struct ProcInfo(int Parent, long Created, uint Session, string Exe, string? OwnerSid = null);

internal static class ProcessRules
{
    // Windows pieces that must never get an End button. Everything else is fair game and simply respawns if Windows needs it.
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "memcompression", "registry", "secure system", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "msmpeng", "audiodg", "sihost",
        "taskhostw", "ctfmon", "ntoskrnl",
    };

    public static bool IsProtectedName(string exeNoExt) => ProtectedNames.Contains(exeNoExt);

    public static bool IsExplorer(string exeNoExt) => exeNoExt.Equals("explorer", StringComparison.OrdinalIgnoreCase);

    // A person's account id: a local, domain or Microsoft account (S-1-5-21-a-b-c-rid) or a work/school account (S-1-12-1-a-b-c-d).
    // Windows' own accounts (SYSTEM, services, built-in groups, Everyone) are never a person, so they never match.
    private static readonly System.Text.RegularExpressions.Regex AccountSid = new(
        @"^S-1-(5-21|12-1)(-[0-9]{1,10}){4}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsAccountSid(string? sid) => sid != null && AccountSid.IsMatch(sid);

    /// <summary>Account ids (SIDs) compare as text, ignoring case.</summary>
    public static bool SameSid(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
