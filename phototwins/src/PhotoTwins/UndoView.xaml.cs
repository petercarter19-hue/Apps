using System.Windows;
using System.Windows.Controls;

namespace PhotoTwins;

public partial class UndoView : UserControl
{
    public event Action? CloseRequested;
    public UndoView() { InitializeComponent(); }
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
