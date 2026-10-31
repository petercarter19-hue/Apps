using System.Windows;
using System.Windows.Controls;

namespace PhotoTwins;

/// <summary>The question the user answers before photos are removed. The button that Enter presses is the safe one when there is a safer choice.</summary>
public partial class ConfirmView : UserControl
{
    public event Action<DialogChoice>? Chosen;

    public ConfirmView(ConfirmRequest request)
    {
        InitializeComponent();
        MessageText.Text = request.Message;
        DetailText.Text = request.Detail ?? "";
        DetailText.Visibility = string.IsNullOrEmpty(request.Detail) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = request.PrimaryText;
        CancelButton.Content = request.CancelText;
        if (request.SecondaryText == null) SecondaryButton.Visibility = Visibility.Collapsed; else SecondaryButton.Content = request.SecondaryText;

        // The default button (the one Enter presses) is highlighted. If the default is "Cancel", the main action is highlighted instead.
        var accent = (Style)Application.Current.FindResource("AccentButtonStyle");
        var highlighted = request.Default == DialogChoice.Cancel ? DialogChoice.Primary : request.Default;
        (highlighted switch { DialogChoice.Primary => PrimaryButton, DialogChoice.Secondary => SecondaryButton, _ => CancelButton }).Style = accent;
        PrimaryButton.IsDefault = request.Default == DialogChoice.Primary;
        SecondaryButton.IsDefault = request.Default == DialogChoice.Secondary;
        CancelButton.IsDefault = request.Default == DialogChoice.Cancel;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => Chosen?.Invoke(DialogChoice.Primary);
    private void OnSecondary(object sender, RoutedEventArgs e) => Chosen?.Invoke(DialogChoice.Secondary);
    private void OnCancel(object sender, RoutedEventArgs e) => Chosen?.Invoke(DialogChoice.Cancel);
}
