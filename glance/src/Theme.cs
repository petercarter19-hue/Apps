using Microsoft.Win32;

namespace Glance;

internal sealed record Theme(
    Color Bg, Color Text, Color TextMuted, Color Separator, Color Track,
    Color Accent, Color AccentBg, Color Danger, Color DangerBg, Color ButtonBorder, Color HoverBg)
{
    public static readonly Theme Light = new(
        Bg: Color.FromArgb(249, 249, 249), Text: Color.FromArgb(26, 26, 26), TextMuted: Color.FromArgb(112, 112, 112),
        Separator: Color.FromArgb(228, 228, 228), Track: Color.FromArgb(230, 230, 230),
        Accent: Color.FromArgb(24, 95, 165), AccentBg: Color.FromArgb(230, 241, 251),
        Danger: Color.FromArgb(163, 45, 45), DangerBg: Color.FromArgb(252, 235, 235),
        ButtonBorder: Color.FromArgb(196, 196, 196), HoverBg: Color.FromArgb(240, 240, 240));

    public static readonly Theme Dark = new(
        Bg: Color.FromArgb(32, 32, 32), Text: Color.FromArgb(240, 240, 240), TextMuted: Color.FromArgb(160, 160, 160),
        Separator: Color.FromArgb(58, 58, 58), Track: Color.FromArgb(62, 62, 62),
        Accent: Color.FromArgb(133, 183, 235), AccentBg: Color.FromArgb(12, 68, 124),
        Danger: Color.FromArgb(240, 149, 149), DangerBg: Color.FromArgb(80, 28, 28),
        ButtonBorder: Color.FromArgb(96, 96, 96), HoverBg: Color.FromArgb(46, 46, 46));

    /// <summary>Follows the Windows "app mode" light/dark setting.</summary>
    public static Theme Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v && v == 0) return Dark;
        }
        catch { }
        return Light;
    }
}
