using System.Windows;
using System.Windows.Media;
using PhotoTwins.Tests.Support;

namespace PhotoTwins.Tests;

/// <summary>The app's own pictures of itself: drawn off-screen (no real screen, mouse or keyboard), at 100%, 125%, and 150%, light and dark, and checked.</summary>
public static class ScreenShots
{
    public static string Folder(string step) => Path.Combine(TestWorkspace.TestWorkRoot(), "pictures", step);

    /// <summary>Draws the main screen at each theme and scale and fails if any text is cut off or hard to read.</summary>
    /// <param name="backdrop">What is behind the window for light (false) and dark (true): a wallpaper as Windows' backdrop would show it. Default: the plain page color.</param>
    public static void Take(MainViewModel vm, string step, string name, double width = 1180, double height = 780, Func<bool, Brush>? backdrop = null)
    {
        foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                WpfHost.SetTheme(dark);
                var png = Path.Combine(Folder(step), $"{name}-{(dark ? "dark" : "light")}-{(int)(scale * 100)}.png");
                var problems = WpfHost.Run(() =>
                {
                    var view = new MainView { DataContext = vm };
                    SelfCheck.RenderToPng(view, width, height, scale, png, backdrop?.Invoke(dark));
                    var p = SelfCheck.FindCutOffText(view);
                    p.AddRange(SelfCheck.FindLowContrastText(view, SelfCheck.PageBackground()));
                    p.AddRange(GlassChecks.WorstCase(view));
                    return p;
                });
                Assert.True(File.Exists(png) && new FileInfo(png).Length > 5000, $"picture not drawn: {png}");
                Assert.True(problems.Count == 0, $"Text is cut off or hard to read in {name} ({(dark ? "dark" : "light")}, {scale * 100}%): " + string.Join(" | ", problems));
            }
        WpfHost.SetTheme(false);
    }

    /// <summary>Draws a viewer the same way.</summary>
    public static void TakeViewer(ViewerViewModel vm, string step, string name)
    {
        foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                WpfHost.SetTheme(dark);
                var png = Path.Combine(Folder(step), $"{name}-{(dark ? "dark" : "light")}-{(int)(scale * 100)}.png");
                var problems = WpfHost.Run(() =>
                {
                    var view = new ViewerView { DataContext = vm };
                    SelfCheck.RenderToPng(view, 1100, 760, scale, png);
                    var p = SelfCheck.FindCutOffText(view);
                    p.AddRange(SelfCheck.FindLowContrastText(view, SelfCheck.PageBackground()));
                    p.AddRange(GlassChecks.WorstCase(view));
                    return p;
                });
                Assert.True(File.Exists(png) && new FileInfo(png).Length > 5000, $"picture not drawn: {png}");
                Assert.True(problems.Count == 0, $"Text is cut off or hard to read in the viewer ({(dark ? "dark" : "light")}, {scale * 100}%): " + string.Join(" | ", problems));
            }
        WpfHost.SetTheme(false);
    }

    /// <summary>Draws a question window's content the same way.</summary>
    public static void TakeConfirm(ConfirmRequest request, string name, Func<bool, Brush>? backdrop = null, string step = "step3")
    {
        foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                WpfHost.SetTheme(dark);
                var png = Path.Combine(Folder(step), $"{name}-{(dark ? "dark" : "light")}-{(int)(scale * 100)}.png");
                var problems = WpfHost.Run(() =>
                {
                    var view = new ConfirmView(request);
                    view.Measure(new System.Windows.Size(640, double.PositiveInfinity));   // how tall does the question need to be at this width?
                    SelfCheck.RenderToPng(view, 640, Math.Ceiling(view.DesiredSize.Height), scale, png, backdrop?.Invoke(dark));
                    var p = SelfCheck.FindCutOffText(view);
                    p.AddRange(SelfCheck.FindLowContrastText(view, SelfCheck.PageBackground()));
                    p.AddRange(GlassChecks.WorstCase(view));
                    return p;
                });
                Assert.True(File.Exists(png) && new FileInfo(png).Length > 2000, $"picture not drawn: {png}");
                Assert.True(problems.Count == 0, $"Text is cut off or hard to read in {name} ({(dark ? "dark" : "light")}, {scale * 100}%): " + string.Join(" | ", problems));
            }
        WpfHost.SetTheme(false);
    }

    /// <summary>Draws the Undo list the same way.</summary>
    public static void TakeUndo(UndoViewModel vm, string name)
    {
        foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            {
                WpfHost.SetTheme(dark);
                var png = Path.Combine(Folder("step3"), $"{name}-{(dark ? "dark" : "light")}-{(int)(scale * 100)}.png");
                var problems = WpfHost.Run(() =>
                {
                    var view = new UndoView { DataContext = vm };
                    SelfCheck.RenderToPng(view, 940, 640, scale, png);
                    var p = SelfCheck.FindCutOffText(view);
                    p.AddRange(SelfCheck.FindLowContrastText(view, SelfCheck.PageBackground()));
                    p.AddRange(GlassChecks.WorstCase(view));
                    return p;
                });
                Assert.True(File.Exists(png) && new FileInfo(png).Length > 2000, $"picture not drawn: {png}");
                Assert.True(problems.Count == 0, $"Text is cut off or hard to read in the Undo list ({(dark ? "dark" : "light")}, {scale * 100}%): " + string.Join(" | ", problems));
            }
        WpfHost.SetTheme(false);
    }

    /// <summary>Asks for the pictures of the first groups on the screen and waits until they are drawn (or known to be undrawable).</summary>
    public static void WaitForPictures(IEnumerable<GroupVm> groups, int maxGroups = 6, int seconds = 30)
    {
        var cards = groups.Take(maxGroups).SelectMany(g => g.Cards).ToList();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            bool all = WpfHost.Run(() => cards.All(c => c.Picture != null || c.PictureStatus.StartsWith("Couldn't")));
            if (all) return;
            Thread.Sleep(50);
        }
        throw new TimeoutException("pictures were not drawn in time");
    }
}
