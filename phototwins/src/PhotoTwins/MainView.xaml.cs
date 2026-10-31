using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PhotoTwins;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnVmChanged;
            foreach (var list in Lists) Pack(list);
        };
        foreach (var list in Lists) list.SizeChanged += (_, _) => Pack(list);
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private ItemsControl[] Lists => new[] { ExactList, SameList, LookAlikeList };

    /// <summary>What the list's scroll bar, padding and margins take from its width.</summary>
    private static double ListChrome => 8 + 6 + SystemParameters.VerticalScrollBarWidth + 2;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ExactGroups)) Pack(ExactList);
        else if (e.PropertyName == nameof(MainViewModel.SameGroups)) Pack(SameList);
        else if (e.PropertyName == nameof(MainViewModel.LookAlikeGroups)) Pack(LookAlikeList);
    }

    /// <summary>Lays a list's groups out in rows as wide as the list: small groups side by side, no big empty space beside them.</summary>
    private void Pack(ItemsControl list)
    {
        if (DataContext is not MainViewModel vm || list.ActualWidth <= 0) return;
        var groups = list == ExactList ? vm.ExactGroups : list == SameList ? vm.SameGroups : vm.LookAlikeGroups;
        var previous = list.ItemsSource as IReadOnlyList<GroupRow>;
        var rows = GroupRows.Build(groups, list.ActualWidth - ListChrome, previous);
        if (!ReferenceEquals(rows, previous)) list.ItemsSource = rows;
    }

    private void OnChangeFolders(object sender, RoutedEventArgs e)
    {
        var win = new FoldersWindow(Vm.Services.Folders) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
        if (win.Changed) Vm.RefreshFolders();
    }

    private void OnShowProblems(object sender, RoutedEventArgs e)
    {
        if (Vm.Result == null) return;
        new ProblemsWindow(Vm.Result, Vm.Services.Log) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        var win = new UndoWindow(Vm.Services, Vm) { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    private void OnShowRemovalDetails(object sender, RoutedEventArgs e)
    {
        if (Vm.LastRemoval == null) return;
        new ProblemsWindow(Vm.LastRemoval, Vm.Services.Log) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>Clicking a picture opens it full size, with the rest of its group a keystroke away.</summary>
    private void OnPictureClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CardVm card) return;
        var cards = card.Group.Cards;
        OpenViewer(cards, cards.ToList().IndexOf(card));
        e.Handled = true;
    }

    private void OpenViewer(IReadOnlyList<CardVm> cards, int index)
    {
        new ViewerWindow(cards, index, Vm.Services) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
