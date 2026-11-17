namespace Spotlight;

/// <summary>The three fixed strengths of version 1.0.0. Only used to read an old settings.json; the slider (0 to 100) replaced them.</summary>
internal enum DimStrength { Light, Medium, Strong }

internal static class Strengths
{
    public const int Default = 55;

    /// <summary>The old Light, Medium, Strong choices as slider values (the same darkness they had).</summary>
    public static int FromLegacy(DimStrength s) => s switch { DimStrength.Light => 35, DimStrength.Strong => 75, _ => 55 };

    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>How dark the dimming gets, 0 to 1, in a straight line: 0% is completely clear, 100% is a full blackout.</summary>
    public static double Level(int percent) => Clamp(percent) / 100.0;

    /// <summary>The overlay's opacity byte for a slider value and the animation's current opacity.</summary>
    public static byte Alpha(int percent, double opacity) =>
        (byte)Math.Clamp((int)Math.Round(Level(percent) * Math.Clamp(opacity, 0, 1) * 255), 0, 255);
}

/// <summary>A hotkey written as text, like "Ctrl+Alt+S". Modifier bits match Windows (Alt 1, Ctrl 2, Shift 4, Win 8).</summary>
internal readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static readonly Hotkey Default = new(1 | 2, 'S');

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0, key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= 2; break;
                case "alt": mods |= 1; break;
                case "shift": mods |= 4; break;
                case "win" or "windows": mods |= 8; break;
                default:
                    if (key != 0) return false; // two keys
                    if (raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0])) key = char.ToUpperInvariant(raw[0]);
                    else if (raw.Length is 2 or 3 && (raw[0] is 'F' or 'f') && int.TryParse(raw.AsSpan(1), out int f) && f is >= 1 and <= 12) key = (uint)(0x6F + f);
                    else return false;
                    break;
            }
        }
        // Needs Ctrl, Alt or Win, so a plain key (or Shift+letter) can never be taken over.
        if (key == 0 || (mods & (1 | 2 | 8)) == 0) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>The hotkey for a key the user pressed in the settings window, or false if it is not allowed (see TryParse).</summary>
    public static bool TryFromKeys(bool ctrl, bool alt, bool shift, bool win, uint virtualKey, out Hotkey hotkey)
    {
        hotkey = default;
        bool isLetterOrDigit = virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9';
        bool isFunction = virtualKey is >= 0x70 and <= 0x7B;
        if (!isLetterOrDigit && !isFunction) return false;
        uint mods = (alt ? 1u : 0) | (ctrl ? 2u : 0) | (shift ? 4u : 0) | (win ? 8u : 0);
        if ((mods & (1 | 2 | 8)) == 0) return false;
        hotkey = new Hotkey(mods, virtualKey);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");
        parts.Add(VirtualKey is >= 0x70 and <= 0x7B ? "F" + (VirtualKey - 0x6F) : ((char)VirtualKey).ToString());
        return string.Join("+", parts);
    }
}

/// <summary>The dimming colors on offer (the slider says how dark; this says what color the dark is).</summary>
internal static class Tints
{
    public static readonly string[] Names = { "Black", "Warm", "Blue", "Green", "Purple" };
    private static readonly int[][] Rgb3 = { new[] { 0, 0, 0 }, new[] { 48, 26, 4 }, new[] { 6, 16, 52 }, new[] { 6, 38, 18 }, new[] { 34, 10, 52 } };

    public static int IndexOf(string? name)
    {
        int i = Array.FindIndex(Names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i;
    }

    public static (int R, int G, int B) Color(string? name) { var c = Rgb3[IndexOf(name)]; return (c[0], c[1], c[2]); }

    /// <summary>The color as Windows GDI wants it (blue, green, red packed).</summary>
    public static int ColorRef(string? name) { var (r, g, b) = Color(name); return r | (g << 8) | (b << 16); }
}

/// <summary>
/// The soft glowing edge: instead of one dark layer with a hard hole, several thinner layers whose holes are a little bigger each,
/// so the dimming builds up gradually over a band around the lit window. Plain math, no Windows calls.
/// </summary>
internal static class SoftEdge
{
    public const int LayerCount = 6;
    /// <summary>How far the band reaches at 100% display scale, in pixels.</summary>
    public const int WidthAt100 = 22;

    /// <summary>
    /// For each layer: how many pixels (at 100%) its hole is bigger than the window, and its opacity (0 to 1).
    /// Stacked, they dim by totalDim far from the window and in equal steps toward it.
    /// </summary>
    public static (int Grow, double Alpha)[] Plan(double totalDim)
    {
        double d = Math.Clamp(totalDim, 0, 1);
        var plan = new (int, double)[LayerCount];
        for (int j = 0; j < LayerCount; j++)
        {
            double before = 1 - d * j / LayerCount, after = 1 - d * (j + 1) / LayerCount;
            plan[j] = ((int)Math.Round(WidthAt100 * (double)j / (LayerCount - 1)), 1 - after / before);
        }
        return plan;
    }
}

/// <summary>Everything the dimming engine needs from the settings, as plain data.</summary>
internal sealed record EngineOptions(
    int DimPercent, string Tint, bool SoftEdge, bool OneScreenOnly, bool HideFromCapture,
    IReadOnlySet<string> SkipApps, Box? Area, bool LightNotifications = false, bool KeepShellBright = false)
{
    public static readonly EngineOptions Default = new(Strengths.Default, "Black", false, false, false, new HashSet<string>(), null);
}

/// <summary>A window that is lit in addition to the lit window: the taskbar, a tray pop-up, a menu, a notification. Square = no rounded corners.</summary>
internal readonly record struct ExtraLight(Box Frame, bool Square);
