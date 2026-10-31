using System.Windows;
using Microsoft.Win32;
using PhotoTwins.Core.Matching;
using PhotoTwins.Core.Settings;

namespace PhotoTwins;

public partial class FoldersWindow : Window
{
    private readonly FolderList _folders;

    /// <summary>True if the list of folders to scan changed (the screen then clears its results).</summary>
    public bool Changed { get; private set; }

    private bool _loading;

    public FoldersWindow(FolderList folders)
    {
        _folders = folders;
        InitializeComponent();
        Glass.Apply(this);
        Refresh();
        _loading = true;
        LookAlikesCheck.IsChecked = _folders.LookAlikesOn;
        StrictStrict.IsChecked = _folders.LookAlikeStrictness == Strictness.Strict;
        StrictNormal.IsChecked = _folders.LookAlikeStrictness == Strictness.Normal;
        StrictLoose.IsChecked = _folders.LookAlikeStrictness == Strictness.Loose;
        StrictnessPanel.IsEnabled = _folders.LookAlikesOn;
        _loading = false;
    }

    private Strictness ChosenStrictness => StrictStrict.IsChecked == true ? Strictness.Strict : StrictLoose.IsChecked == true ? Strictness.Loose : Strictness.Normal;

    /// <summary>Look-alikes on or off, and how strict. Saved at once; the next scan uses it.</summary>
    private void OnLookAlikeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = LookAlikesCheck.IsChecked == true;
        StrictnessPanel.IsEnabled = on;
        var why = _folders.SetLookAlikes(on, ChosenStrictness);
        MessageText.Text = why ?? "";
        if (why == null) Changed = true;   // the screen clears its results: the next scan uses the new setting
    }

    private void Refresh()
    {
        FolderListBox.ItemsSource = _folders.Describe();
        RemoveButton.IsEnabled = _folders.Folders.Count > 0;
        SharedListBox.ItemsSource = _folders.SharedFolders.ToList();
        RemoveSharedButton.IsEnabled = _folders.SharedFolders.Count > 0;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick folders to scan", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        var refused = new List<string>();
        foreach (var path in dlg.FolderNames)
        {
            var why = _folders.TryAdd(path);
            if (why == null) Changed = true;
            else refused.Add($"\"{path}\": {why}");
        }
        MessageText.Text = string.Join("\n", refused);
        Refresh();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (FolderListBox.SelectedItem is FolderStatus s)
        {
            var why = _folders.Remove(s.Path);
            if (why == null) Changed = true;
            MessageText.Text = why ?? "";
            Refresh();
        }
        else MessageText.Text = "Click a folder in the list first, then Remove.";
    }

    private void OnAddShared(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick folders you share with other people", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        var refused = new List<string>();
        foreach (var path in dlg.FolderNames)
        {
            var why = _folders.TryAddShared(path);
            if (why != null) refused.Add($"\"{path}\": {why}");
        }
        MessageText.Text = string.Join("\n", refused);
        Refresh();
    }

    private void OnRemoveShared(object sender, RoutedEventArgs e)
    {
        if (SharedListBox.SelectedItem is string s)
        {
            MessageText.Text = _folders.RemoveShared(s) ?? "";
            Refresh();
        }
        else MessageText.Text = "Click a folder in the shared list first, then Remove.";
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
