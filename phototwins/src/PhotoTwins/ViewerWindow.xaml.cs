using System.Windows;
using System.Windows.Input;

namespace PhotoTwins;

public partial class ViewerWindow : Window
{
    public ViewerViewModel Model { get; }

    public ViewerWindow(IReadOnlyList<CardVm> cards, int index, AppServices services)
    {
        InitializeComponent();
        Glass.Apply(this);
        Model = new ViewerViewModel(cards, index, services);
        View.DataContext = Model;
        View.CloseRequested += Close;
        PreviewKeyDown += (_, e) => { if (View.HandleKey(e.Key)) e.Handled = true; };
        Loaded += (_, _) => _ = Model.LoadCurrentAsync();
    }
}
