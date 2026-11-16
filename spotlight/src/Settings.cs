using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Spotlight;

/// <summary>Spotlight's own settings: on or off, how dark, the hotkey. The only thing it ever saves (besides a small log).</summary>
internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    /// <summary>How dark, 0 to 100 (the slider in the settings window).</summary>
    public int DimPercent { get; set; } = Strengths.Default;
    /// <summary>Only read from a version 1.0.0 file (Light, Medium, Strong); turned into DimPercent on load and never written back.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DimStrength? Strength { get; set; }
    /// <summary>The dimming color: Black, Warm, Blue, Green or Purple.</summary>
    public string Tint { get; set; } = "Black";
    /// <summary>Dim only the screen the light is on (several screens).</summary>
    public bool OneScreenOnly { get; set; }
    /// <summary>A soft glowing edge around the lit window instead of a hard one.</summary>
    public bool SoftEdge { get; set; }
    /// <summary>Leave the dimming out of screen sharing and recordings (people watching your screen see no dimming).</summary>
    public bool HideFromCapture { get; set; }
    /// <summary>Show the quick bar: a small floating pill with the common controls.</summary>
    public bool QuickBar { get; set; } = true;
    /// <summary>The quick bar's look: Dark (smoked glass) or Light (frosted glass).</summary>
    public string QuickBarTheme { get; set; } = "Dark";
    /// <summary>Where the user left the quick bar (screen pixels, top-left corner); null = the first-run spot.</summary>
    public int? QuickBarX { get; set; }
    public int? QuickBarY { get; set; }
    /// <summary>Keep the taskbar, the tray pop-up (the ^ arrow), jump lists and pop-up menus lit, so they are never hard to see.</summary>
    public bool KeepShellBright { get; set; } = true;
    /// <summary>When a Windows notification pops up (email, Teams, other apps), light it too instead of dimming it.</summary>
    public bool LightNotifications { get; set; }
    /// <summary>Do not dim while a video-meeting app (Zoom, Teams, Webex...) is in front.</summary>
    public bool SkipMeetingApps { get; set; }
    /// <summary>Programs that are never dimmed around (names like "zoom", no .exe).</summary>
    public List<string> NeverDimApps { get; set; } = new();
    /// <summary>Spotlight is paused until this moment (UTC); null = not paused.</summary>
    public DateTime? PausedUntilUtc { get; set; }
    /// <summary>true = the light stays on the picked Area instead of following windows.</summary>
    public bool AreaMode { get; set; }
    /// <summary>The picked area: left, top, right, bottom in screen pixels; null = none picked yet.</summary>
    public int[]? Area { get; set; }
    /// <summary>Written like "Ctrl+Alt+S". Change it in the settings window.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+S";

    [JsonIgnore] public bool FirstRun { get; private set; }

    [JsonIgnore] public bool HasArea => Area is { Length: 4 } a && a[2] > a[0] && a[3] > a[1];
    [JsonIgnore] public Box? AreaBox => HasArea ? new Box(Area![0], Area[1], Area[2], Area[3]) : null;

    public bool IsPaused(DateTime nowUtc) => PausedUntilUtc is { } until && until > nowUtc;

    /// <summary>On, and not paused: what the dimming engine should do right now.</summary>
    public bool Active(DateTime nowUtc) => Enabled && !IsPaused(nowUtc);

    /// <summary>What the dimming engine needs, as plain data.</summary>
    public EngineOptions ToOptions()
    {
        var skip = new HashSet<string>((NeverDimApps ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(Rules.NormalizeApp).Where(n => n.Length > 0));
        if (SkipMeetingApps) foreach (var m in Rules.MeetingApps) skip.Add(m);
        return new EngineOptions(Strengths.Clamp(DimPercent), Tint, SoftEdge, OneScreenOnly, HideFromCapture, skip, AreaMode ? AreaBox : null, LightNotifications, KeepShellBright);
    }

    public static string Dir => Instance.StateDir;
    internal static string FilePath => Path.Combine(Dir, "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [JsonIgnore] public Hotkey ParsedHotkey => Spotlight.Hotkey.TryParse(Hotkey, out var h) ? h : Spotlight.Hotkey.Default;

    /// <summary>Set when settings.json could not be read in full on the last Load (a damaged file); the tray shows it once.</summary>
    /// <summary>True on the stand-in returned when settings.json exists but cannot be read and there is no earlier good copy: dimming stays off until it can be read.</summary>
    [JsonIgnore] public bool Unavailable { get; private set; }

    private static Settings Unreadable()
    {
        _problem ??= "Spotlight could not read its settings file just now, so it is staying off until it can. It will try again by itself.";
        return new Settings { Enabled = false, Unavailable = true };
    }

    private static string? _problem;
    public static string? TakeProblem() { var p = _problem; _problem = null; return p; }

    private static string? _lastGoodJson;
    internal static void ForgetLastGood() => _lastGoodJson = null; // for tests: act like a fresh start of the program

    /// <summary>Reads settings.json, looking again a few times if it is busy or half written. Null if it could not be read at all.</summary>
    private static string? ReadFileText(out Exception? last)
    {
        string? text = null; last = null;
        for (int i = 0; i < 6 && text is null; i++)
        {
            try { text = File.ReadAllText(FilePath); }
            catch (IOException ex) { last = ex; Thread.Sleep(40); }
            catch (UnauthorizedAccessException ex) { last = ex; Thread.Sleep(40); }
        }
        return text;
    }

    /// <summary>Turns the text of settings.json into settings: exact when it reads fine, tolerant (keeping every good field) when it is damaged.</summary>
    private static Settings Parse(string text)
    {
        try { var ok = Normalize(text); _lastGoodJson = text; return ok; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { Thread.Sleep(60); text = ReadFileText(out _) ?? text; } // a damaged file, or one holding just "null" // maybe another copy was just mid-write
        return NormalizeTolerant(text);
    }

    /// <summary>
    /// "No settings.json" is only believed under the same lock edits use: another copy may be between two steps of a save. Seen again
    /// under the lock, a missing file really is a first run and the usual choices are saved.
    /// </summary>
    private static Settings LoadOrCreateUnderLock()
    {
        using var gate = new Mutex(false, Instance.SettingsLockName);
        bool held = false;
        try { held = gate.WaitOne(4000); }
        catch (AbandonedMutexException) { held = true; }
        try
        {
            if (!held) return _lastGoodJson is { } g ? Normalize(g) : Unreadable();
            if (File.Exists(FilePath))
            {
                var text = ReadFileText(out _);
                return text is not null ? Parse(text) : (_lastGoodJson is { } g2 ? Normalize(g2) : Unreadable());
            }
            var fresh = new Settings { FirstRun = true };
            fresh.Save();
            return fresh;
        }
        finally { if (held) { try { gate.ReleaseMutex(); } catch { } } }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // The file may be busy (another copy is writing it) or half written: look again a few times before believing it.
                var text = ReadFileText(out var last);
                if (text is null)
                {
                    // Could not read it at all just now: keep what was last read fine, never fall back to the usual choices over a busy file.
                    Log.WriteOnce(last ?? new IOException("settings.json could not be read"));
                    return _lastGoodJson is { } g ? Normalize(g) : Unreadable();
                }
                return Parse(text);
            }
            return LoadOrCreateUnderLock();
        }
        catch (Exception ex) { Log.Write(ex); }
        return _lastGoodJson is { } g2 ? Normalize(g2) : Unreadable();
    }

    /// <summary>
    /// Read the newest settings.json, change it and save it as ONE step that no other copy of Spotlight can interleave with (a named lock
    /// shared by the tray copy, the settings window and the area picker). Returns the new settings and whether the save worked.
    /// </summary>
    public static (Settings Settings, bool Saved) Edit(Action<Settings> change)
    {
        using var gate = new Mutex(false, Instance.SettingsLockName);
        bool held = false;
        try { held = gate.WaitOne(4000); }
        catch (AbandonedMutexException) { held = true; } // a copy died holding it: the file is intact (it is replaced whole)
        try
        {
            if (!held)
            {
                // Another copy still holds the lock: changing now could overwrite its newer choice. Do nothing and say so.
                Log.WriteOnce(new TimeoutException("settings.json was busy: the change was not applied"));
                _problem ??= "Spotlight could not save that change because its settings file was busy. Please try again.";
                return (Load(), false);
            }
            string? current = null;
            if (File.Exists(FilePath) && (current = ReadFileText(out var readError)) is null)
            {
                // The file exists but cannot be read right now: an edit built on defaults would overwrite the user's real choices.
                Log.WriteOnce(readError ?? new IOException("settings.json could not be read"));
                _problem ??= "Spotlight could not save that change because its settings file could not be read just now. Please try again.";
                return (Load(), false);
            }
            var s = current is null ? Load() : Parse(current); // the text just read: no second read that could fall back to defaults
            change(s);
            bool saved = s.Save();
            return (s, saved);
        }
        finally { if (held) { try { gate.ReleaseMutex(); } catch { } } }
    }

    /// <summary>
    /// Reads the newest settings.json, applies one change, saves it, and tells the running tray copy to re-read it.
    /// Used by the settings window and the area picker, so neither overwrites what the other just saved.
    /// </summary>
    public static bool Change(Action<Settings> change)
    {
        var (_, saved) = Edit(change);
        try { using var e = new EventWaitHandle(false, EventResetMode.AutoReset, Instance.ReloadEventName); e.Set(); }
        catch (Exception ex) { Log.Write(ex); }
        return saved;
    }

    /// <summary>Reads settings.json text. A version 1.0.0 file (Strength, no DimPercent) keeps its darkness; a bad number is pulled into range.</summary>
    internal static Settings Normalize(string json)
    {
        var s = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        bool hasPercent = JsonDocument.Parse(json).RootElement.TryGetProperty("DimPercent", out _);
        return Fix(s, hasPercent);
    }

    private static Settings Fix(Settings s, bool hasPercent)
    {
        if (!hasPercent && s.Strength is { } legacy) s.DimPercent = Strengths.FromLegacy(legacy);
        s.Strength = null;
        s.DimPercent = Strengths.Clamp(s.DimPercent);
        s.Tint = Tints.Names[Tints.IndexOf(s.Tint)];
        s.QuickBarTheme = QuickBarLayout.NormalizeTheme(s.QuickBarTheme);
        s.NeverDimApps = (s.NeverDimApps ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Rules.NormalizeApp(x)).Where(x => x.Length > 0).Distinct().ToList();
        if (!s.HasArea) s.Area = null;
        return s;
    }

    /// <summary>
    /// A damaged file: keep every field that still reads fine (one bad field must not cost the user all the others), save a copy of the
    /// damaged file as settings.bad.json, and remember a message so the tray can say so once.
    /// </summary>
    internal static Settings NormalizeTolerant(string json)
    {
        var s = new Settings(); bool hasPercent = false, skipped = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in typeof(Settings).GetProperties())
                {
                    if (!prop.CanWrite || prop.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).Any(a => ((JsonIgnoreAttribute)a).Condition == JsonIgnoreCondition.Always)) continue;
                    var match = doc.RootElement.EnumerateObject().FirstOrDefault(x => x.Name == prop.Name);
                    if (match.Value.ValueKind == JsonValueKind.Undefined) continue;
                    try { prop.SetValue(s, JsonSerializer.Deserialize(match.Value.GetRawText(), prop.PropertyType)); if (prop.Name == "DimPercent") hasPercent = true; }
                    catch (Exception) { skipped = true; }
                }
            }
            else skipped = true;
        }
        catch (JsonException) { skipped = true; }
        if (skipped)
        {
            try { File.Copy(FilePath, Path.Combine(Dir, "settings.bad.json"), true); } catch (Exception ex) { Log.Write(ex); }
            _problem = "Spotlight could not read part of its settings file, so it used the usual choices for that part. A copy of the damaged file was saved next to it as settings.bad.json.";
            Log.Write(new InvalidDataException("settings.json was damaged; kept what could be read, saved settings.bad.json"));
        }
        return Fix(s, hasPercent);
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Write a new file beside it and swap it in, so nobody ever reads half a file.
            string tmp = FilePath + ".tmp";
            string text = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, text);
            // Move over the old file in one step (the name never goes missing, unlike File.Replace); another copy reading it at that
            // very moment can make the move fail briefly, so try a few times.
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, FilePath, true); break; }
                catch (Exception ex) when (i < 5 && (ex is IOException || ex is UnauthorizedAccessException)) { Thread.Sleep(30); }
            }
            _lastGoodJson = text;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _problem ??= "Spotlight could not save your settings just now (the file may be locked or the disk full). Your last choices are still in use.";
            return false;
        }
    }

    // "Start at sign-in" uses the per-user Run key, so it shows in Task Manager's Startup tab like any other app.
    // A test copy keeps its "startup entry" in a file in its own folder instead: it can never register at the real sign-in.
    public static bool IsTempCopy =>
        Environment.ProcessPath?.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) == true;

    private static string StartupFile => Path.Combine(Dir, "startup-entry.txt");

    public static bool StartsAtSignIn()
    {
        try
        {
            if (Instance.IsTest) return File.Exists(StartupFile) && File.ReadAllText(StartupFile).Length > 0;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Instance.EntryName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void SetStartAtSignIn(bool on)
    {
        try
        {
            if (Instance.IsTest)
            {
                Directory.CreateDirectory(Dir);
                if (on) File.WriteAllText(StartupFile, $"\"{Environment.ProcessPath}\"");
                else File.Delete(StartupFile);
                return;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(Instance.EntryName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(Instance.EntryName, false);
        }
        catch (Exception ex) { Log.Write(ex); }
    }

    /// <summary>The installed copy is the one with the installer's uninstaller (unins000.exe) beside it.</summary>
    internal static bool IsInstalledCopy(string? exePath, Func<string, bool> fileExists) =>
        !string.IsNullOrEmpty(exePath) && Path.GetDirectoryName(exePath) is { Length: > 0 } d && fileExists(Path.Combine(d, "unins000.exe"));

    /// <summary>
    /// If start-at-sign-in is on but points at an old place (the install moved), point it at this copy.
    /// Only the installed copy may do this: a portable or test copy that merely runs must never take over the entry.
    /// </summary>
    public static void RefreshStartupPath()
    {
        try
        {
            string want = $"\"{Environment.ProcessPath}\"";
            if (Instance.IsTest)
            {
                if (File.Exists(StartupFile) && File.ReadAllText(StartupFile) is { Length: > 0 } t && t != want) File.WriteAllText(StartupFile, want);
                return;
            }
            if (!IsInstalledCopy(Environment.ProcessPath, File.Exists)) return;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(Instance.EntryName) is string s && s.Length > 0 && s != want) key.SetValue(Instance.EntryName, want);
        }
        catch (Exception ex) { Log.Write(ex); }
    }
}

internal static class Log
{
    private static readonly HashSet<string> Seen = new();

    /// <summary>Logs each distinct error once (same type and message), so a repeating failure cannot flood log.txt. Returns true if it was new.</summary>
    public static bool WriteOnce(Exception ex)
    {
        bool isNew;
        lock (Seen) isNew = Seen.Add(ex.GetType().FullName + ":" + ex.Message);
        if (isNew) Write(ex);
        return isNew;
    }

    public static void Write(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var logPath = Path.Combine(Settings.Dir, "log.txt");
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 200_000) File.Delete(logPath); // the log never grows without limit
            File.AppendAllText(Path.Combine(Settings.Dir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
