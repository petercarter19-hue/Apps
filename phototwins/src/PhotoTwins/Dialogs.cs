using System.Windows;

namespace PhotoTwins;

public enum DialogChoice { Primary, Secondary, Cancel }

/// <summary>A question for the user before something is removed.</summary>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string PrimaryText,
    string? SecondaryText,
    string CancelText,
    /// <summary>The button pressed by Enter. For the shared-folder question this is the SAFE one (skip), never the removing one.</summary>
    DialogChoice Default,
    string? Detail = null);

/// <summary>How the app asks the user things. The real one opens a window; tests supply a scripted one that records exactly what was asked.</summary>
public interface IDialogs
{
    Task<DialogChoice> ConfirmAsync(ConfirmRequest request);
    void ShowMessage(string title, string message);
}

public sealed class WindowDialogs : IDialogs
{
    private static Window? Owner() => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public Task<DialogChoice> ConfirmAsync(ConfirmRequest request)
    {
        var win = new ConfirmWindow(request) { Owner = Owner() };
        win.ShowDialog();
        return Task.FromResult(win.Choice);
    }

    public void ShowMessage(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
