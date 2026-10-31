using System.Windows;
using System.ComponentModel;

namespace PhotoTwins;

public partial class UndoWindow : Window
{
    public UndoViewModel Model { get; }

    public UndoWindow(AppServices services, IRestoreTarget? target = null)
    {
        InitializeComponent();
        Glass.Apply(this);
        Model = new UndoViewModel(services, target);
        View.DataContext = Model;
        View.CloseRequested += Close;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Model.IsBusy) { e.Cancel = true; return; }
        base.OnClosing(e);
    }
}
