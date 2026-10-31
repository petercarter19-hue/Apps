using System.Windows;

namespace PhotoTwins;

public partial class ConfirmWindow : Window
{
    /// <summary>What the user chose. Closing the window any other way counts as Cancel.</summary>
    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    public ConfirmWindow(ConfirmRequest request)
    {
        InitializeComponent();
        Glass.Apply(this);
        Title = request.Title;
        var view = new ConfirmView(request);
        view.Chosen += c => { Choice = c; Close(); };
        Host.Content = view;
    }
}
