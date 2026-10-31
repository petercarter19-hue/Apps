using System.Windows;
using System.Windows.Media;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>The screen follows the Windows light or dark setting through the standard theme; every colour name the screen asks for must exist in both.</summary>
[Collection("WPF")]
public class ThemeTests
{
    private static readonly string[] KeysUsed =
    {
        "ApplicationBackgroundBrush", "CardBackgroundFillColorDefaultBrush", "CardStrokeColorDefaultBrush",
        "TextFillColorSecondaryBrush", "AccentTextFillColorPrimaryBrush", "SystemFillColorCriticalBrush", "AccentButtonStyle",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_colour_and_style_the_screen_uses_exists_in_light_and_dark(bool dark)
    {
        WpfHost.SetTheme(dark);
        var missing = WpfHost.Run(() => KeysUsed.Where(k => Application.Current.TryFindResource(k) == null).ToList());
        Assert.Empty(missing);
    }

    [Fact]
    public void Dark_really_is_dark_and_light_really_is_light()
    {
        double Lum(bool dark)
        {
            WpfHost.SetTheme(dark);
            return WpfHost.Run(() =>
            {
                var c = ((SolidColorBrush)Application.Current.FindResource("ApplicationBackgroundBrush")).Color;
                return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            });
        }
        var light = Lum(false);
        var dark = Lum(true);
        WpfHost.SetTheme(false);
        Assert.True(light > 200, $"light background luminance {light}");
        Assert.True(dark < 60, $"dark background luminance {dark}");
    }
}
