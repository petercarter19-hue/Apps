using System.Diagnostics;
using System.Windows;
using PhotoTwins.Core.Logging;
using PhotoTwins.Core.Removal;
using PhotoTwins.Core.Scanning;

namespace PhotoTwins;

public partial class ProblemsWindow : Window
{
    private readonly AppLog _log;

    public ProblemsWindow(ScanResult result, AppLog log)
    {
        _log = log;
        InitializeComponent();
        Glass.Apply(this);
        IntroText.Text = "Nothing here was changed. Each line says why a photo or folder was not included in the scan. The same list is saved in the log.";
        ProblemsHeading.Text = result.Problems.Count > 0 ? $"Couldn't read {Format.Plural(result.Problems.Count, "photo", "photos")}" : "Every photo found could be read.";
        ProblemsItems.ItemsSource = result.Problems;
        LeftOutHeading.Text = result.LeftOut.Count > 0 ? $"Left out: {Format.Plural(result.LeftOut.Count, "folder or link", "folders and links")}" : "";
        LeftOutItems.ItemsSource = result.LeftOut;
    }

    /// <summary>The same window, showing what a removal could not do.</summary>
    public ProblemsWindow(RemovalResult removal, AppLog log)
    {
        _log = log;
        InitializeComponent();
        Glass.Apply(this);
        Title = "What couldn't be removed";
        IntroText.Text = "These photos were NOT removed. Each line says why. Everything else that was removed can be put back from Undo. The same list is saved in the log.";
        ProblemsHeading.Text = removal.Failed.Count > 0 ? $"Couldn't remove {Format.Plural(removal.Failed.Count, "photo", "photos")}" : "";
        ProblemsItems.ItemsSource = removal.Failed.Select(f => new Problem(f.File.Path, f.Why)).ToList();
        LeftOutHeading.Text = removal.Dropped.Count > 0 ? $"Left out because {(removal.Dropped.Count == 1 ? "it" : "they")} changed or disappeared after the scan: {removal.Dropped.Count:N0}" : "";
        LeftOutItems.ItemsSource = removal.Dropped.Select(d => new LeftOut(d.File.Path, d.Why)).ToList();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_log.Folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _log.Error("Couldn't open the log folder", ex); IntroText.Text = "Couldn't open the log folder. It is here: " + _log.Folder; }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
