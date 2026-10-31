using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>The "worst wallpaper" contrast check for the glass look: every piece of text must stay readable over black, white, gray and the strongest colors.</summary>
public static class GlassChecks
{
    /// <summary>What can be behind the window (after Windows' own blur, a wallpaper averages out to something between these).</summary>
    public static readonly (string name, Color color)[] Backdrops =
    {
        ("black", Colors.Black), ("white", Colors.White), ("gray", Color.FromRgb(128, 128, 128)),
        ("red", Color.FromRgb(255, 0, 0)), ("green", Color.FromRgb(0, 255, 0)), ("blue", Color.FromRgb(0, 0, 255)),
        ("yellow", Color.FromRgb(255, 255, 0)), ("cyan", Color.FromRgb(0, 255, 255)), ("magenta", Color.FromRgb(255, 0, 255)),
        ("dark gray", Color.FromRgb(64, 64, 64)), ("light gray", Color.FromRgb(192, 192, 192)),
    };

    /// <summary>Text that is hard to read on any of them. The view must already be hosted (drawn) by SelfCheck.</summary>
    public static List<string> WorstCase(FrameworkElement view)
    {
        var problems = new List<string>();
        var fog = (FrameworkElement)VisualTreeHelper.GetParent(view);
        var host = (Panel)VisualTreeHelper.GetParent(fog);
        var saved = host.Background;
        host.Background = null;                               // nothing solid behind the glass: whatever the wallpaper is
        try
        {
            foreach (var (name, color) in Backdrops)
                foreach (var p in SelfCheck.FindLowContrastText(host, color)) problems.Add($"{p} over {name}");
        }
        finally { host.Background = saved; }
        return problems;
    }
}
