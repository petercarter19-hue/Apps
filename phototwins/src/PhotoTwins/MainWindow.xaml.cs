using System.ComponentModel;
using System.Windows;

namespace PhotoTwins;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closingAfterStop;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Glass.Apply(this, hero: true);
        View.DataContext = vm;
    }

    /// <summary>Closing mid-scan stops it and saves what's done first, so the next launch can continue.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        var restoring = OwnedWindows.OfType<UndoWindow>().Where(w => w.Model.IsBusy).Select(w => w.Model.WaitUntilIdleAsync()).ToArray();
        if (_vm.IsWorking || restoring.Length > 0)
        {
            e.Cancel = true;
            if (_closingAfterStop) return;
            _closingAfterStop = true;
            try
            {
                await _vm.StopAndWaitAsync();
                await Task.WhenAll(restoring);
            }
            finally { _closingAfterStop = false; }
            Close();
            return;
        }
        base.OnClosing(e);
    }
}
