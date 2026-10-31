using Microsoft.Win32;

namespace PhotoTwins.Core.Scanning;

public static class CloudAttributes
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// True for files (or folders) that live only in the cloud: Windows marks them "recall on open",
    /// "recall on data access" or "offline". Reading one would download it, so the scanner never does.
    /// </summary>
    public static bool IsOnlineOnly(FileAttributes a) => (a & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0;
}

public static class PhotoTypes
{
    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".heic", ".heif", ".webp", ".bmp", ".tif", ".tiff",
    };

    public static bool IsPhoto(string fileName) => Exts.Contains(Path.GetExtension(fileName));
}

/// <summary>
/// The folder rules: other people's Windows accounts are never entered, Google Drive's streamed drive is never read,
/// system folders and our own "Duplicates to delete" folder are never scanned.
/// </summary>
public sealed class ScanPolicy
{
    public const string HoldingFolderName = "Duplicates to delete";

    private static readonly string[] AlwaysSkipNames = { "$Recycle.Bin", "RECYCLER", "System Volume Information" };
    private static readonly string[] DriveRootSystemNames = { "Windows", "Program Files", "Program Files (x86)", "ProgramData" };

    private readonly string _currentProfile;
    private readonly string[] _otherProfiles;
    private readonly Func<string, bool> _isGoogleDriveVolume;

    public ScanPolicy(string currentProfile, IEnumerable<string> otherProfiles, Func<string, bool> isGoogleDriveVolume)
    {
        _currentProfile = Normalize(currentProfile);
        _otherProfiles = otherProfiles.Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _isGoogleDriveVolume = isGoogleDriveVolume;
    }

    /// <summary>The real rules for this PC, built from Windows' own list of accounts.</summary>
    public static ScanPolicy ForThisPc()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cur = Normalize(profile);
        var others = new List<string>();
        try
        {
            var usersDir = Path.GetDirectoryName(cur);
            if (!string.IsNullOrEmpty(usersDir) && Directory.Exists(usersDir))
            {
                foreach (var d in Directory.EnumerateDirectories(usersDir))
                {
                    var n = Normalize(d);
                    var name = Path.GetFileName(n);
                    if (n.Equals(cur, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("Public", StringComparison.OrdinalIgnoreCase)) continue;   // shared by design, not a person
                    others.Add(n);
                }
            }
        }
        catch { /* the registry list below still covers us */ }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (k != null)
            {
                foreach (var sid in k.GetSubKeyNames())
                {
                    using var sub = k.OpenSubKey(sid);
                    var raw = sub?.GetValue("ProfileImagePath") as string;
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var n = Normalize(Environment.ExpandEnvironmentVariables(raw));
                    if (n.Equals(cur, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(n).Equals("Public", StringComparison.OrdinalIgnoreCase)) continue;
                    others.Add(n);
                }
            }
        }
        catch { /* as above */ }
        return new ScanPolicy(cur, others, RealIsGoogleDriveVolume);
    }

    /// <summary>Google Drive for desktop shows as a drive labeled "Google Drive" holding "My Drive" and a hidden ".shortcut-targets-by-id" folder.</summary>
    private static bool RealIsGoogleDriveVolume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var label = new DriveInfo(root).VolumeLabel ?? "";
            if (label.StartsWith("Google Drive", StringComparison.OrdinalIgnoreCase)) return true;
            return Directory.Exists(Path.Combine(root, ".shortcut-targets-by-id"));
        }
        catch { return false; }
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length) full = full.TrimEnd('\\', '/');
        return full;
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = f.EndsWith('\\') ? f : f + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsInsideOtherProfile(string path) => _otherProfiles.Any(p => IsSameOrInside(path, p));
    public bool IsGoogleDriveVolume(string path) => _isGoogleDriveVolume(path);

    /// <summary>Why this folder must not be picked as a scan folder, or null if it's fine. Plain English, shown to the user.</summary>
    public string? WhyNotAllowedAsRoot(string path)
    {
        var full = Normalize(path);
        if (IsInsideOtherProfile(full))
            return "That folder belongs to another person's Windows account. PhotoTwins never looks inside other accounts.";
        if (IsGoogleDriveVolume(full))
            return "Google Drive folders are skipped: Windows can't tell which of those photos are stored on this PC, and reading them could download them.";
        if (IsSystemFolderOrInside(full))
            return "That's a Windows system folder. PhotoTwins doesn't scan those.";
        if (IsSameOrInside(full, Path.Combine(_currentProfile, "AppData")))
            return "That's where programs keep their own files. PhotoTwins doesn't scan those.";
        if (Path.GetFileName(full).Equals(HoldingFolderName, StringComparison.OrdinalIgnoreCase)
            || ContainsHoldingFolder(full))
            return "That's PhotoTwins' own \"Duplicates to delete\" holding folder. It isn't scanned.";
        return null;
    }

    private static bool ContainsHoldingFolder(string full) =>
        full.Split('\\', '/').Any(seg => seg.Equals(HoldingFolderName, StringComparison.OrdinalIgnoreCase));

    private static bool IsSystemFolderOrInside(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= root.Length) return false;
        var first = full.Substring(root.Length).Split('\\', '/')[0];
        if (DriveRootSystemNames.Any(n => n.Equals(first, StringComparison.OrdinalIgnoreCase))) return true;
        return full.Split('\\', '/').Any(seg => AlwaysSkipNames.Any(n => n.Equals(seg, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A folder found while walking: should we go in? Returns a plain reason if not, or null to go in.</summary>
    public string? WhyLeaveOut(FileEntry dir)
    {
        if (dir.IsLink) return "It's a shortcut link, which could lead outside the folders you picked.";
        if (CloudAttributes.IsOnlineOnly(dir.Attributes)) return "It's online-only (not stored on this PC).";
        if (dir.Name.Equals(HoldingFolderName, StringComparison.OrdinalIgnoreCase)) return "It's PhotoTwins' \"Duplicates to delete\" holding folder.";
        if (AlwaysSkipNames.Any(n => n.Equals(dir.Name, StringComparison.OrdinalIgnoreCase))) return "It's a Windows system folder.";
        if (IsInsideOtherProfile(dir.FullPath)) return "It belongs to another person's Windows account.";
        var parent = Path.GetDirectoryName(dir.FullPath) ?? "";
        var parentRoot = Path.GetPathRoot(dir.FullPath) ?? "";
        if (Normalize(parent).Equals(Normalize(parentRoot), StringComparison.OrdinalIgnoreCase)
            && DriveRootSystemNames.Any(n => n.Equals(dir.Name, StringComparison.OrdinalIgnoreCase)))
            return "It's a Windows system folder.";
        if (dir.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase)
            && Normalize(parent).Equals(_currentProfile, StringComparison.OrdinalIgnoreCase))
            return "It's where programs keep their own files.";
        return null;
    }

    /// <summary>Is this folder's own listing off limits because it is itself a Google Drive drive (checked for each picked folder).</summary>
    public bool IsOffLimitsVolume(string path) => IsGoogleDriveVolume(path);
}

public readonly record struct RootCheck(string Path, string? Problem)
{
    public bool Ok => Problem == null;
}

public static class ScanRoots
{
    /// <summary>Checks a folder the user picked. Returns the cleaned-up path, or a plain-English reason it can't be used.</summary>
    public static RootCheck Check(string path, IFileSystem fs, ScanPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(path)) return new RootCheck("", "Pick a folder.");
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return new RootCheck(path, "Pick a full folder, such as a folder in File Explorer.");
            full = ScanPolicy.Normalize(path);
        }
        catch (Exception) { return new RootCheck(path, "That doesn't look like a folder."); }
        if (!fs.DirectoryExists(full)) return new RootCheck(full, "That folder doesn't exist (or isn't connected right now).");
        var why = policy.WhyNotAllowedAsRoot(full);
        if (why != null) return new RootCheck(full, why);
        // A folder can be a shortcut link (or sit on a drive-letter shortcut) that really leads somewhere else: judge that place too.
        string real;
        try { real = fs.GetRealPath(full); }
        catch (Exception) { return new RootCheck(full, "PhotoTwins couldn't tell where that folder really leads, so it won't scan it."); }
        if (!string.Equals(ScanPolicy.Normalize(real), full, StringComparison.OrdinalIgnoreCase))
        {
            var whyReal = policy.WhyNotAllowedAsRoot(real);
            if (whyReal != null) return new RootCheck(full, "That folder is a shortcut that really leads to a place PhotoTwins won't scan. " + whyReal);
        }
        return new RootCheck(full, null);
    }

    /// <summary>Drops repeats and folders that sit inside another picked folder, so no photo is looked at twice.</summary>
    public static List<string> Simplify(IEnumerable<string> roots)
    {
        var norm = roots.Select(ScanPolicy.Normalize).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return norm.Where(r => !norm.Any(o => !o.Equals(r, StringComparison.OrdinalIgnoreCase) && ScanPolicy.IsSameOrInside(r, o)))
                   .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
