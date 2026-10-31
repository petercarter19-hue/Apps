using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PhotoTwins.Tests")]

namespace PhotoTwins;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    private readonly Func<bool>? _canRun;
    public RelayCommand(Action run, Func<bool>? canRun = null) { _run = run; _canRun = canRun; }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canRun?.Invoke() ?? true;
    public void Execute(object? parameter) => _run();
    public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public static class Format
{
    /// <summary>Sizes the way Windows shows them: 1,024 steps, one decimal.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return v >= 100 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }

    public static string TimeLeft(TimeSpan t)
    {
        if (t.TotalSeconds < 45) return "less than a minute left";
        if (t.TotalMinutes < 60) return $"about {Math.Max(1, (int)Math.Round(t.TotalMinutes))} min left";
        int h = (int)t.TotalHours;
        int m = (int)Math.Round(t.TotalMinutes - h * 60);
        if (m == 60) { h++; m = 0; }
        return m == 0 ? $"about {h} hr left" : $"about {h} hr {m} min left";
    }

    /// <summary>
    /// Lets long names and folder paths wrap inside a narrow card: adds invisible break points after slashes, dots, dashes and
    /// underscores, and inside very long unbroken runs. (WPF only wraps at spaces, so a long path would otherwise be cut off.)
    /// </summary>
    public static string Breakable(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 8);
        int run = 0;
        foreach (var ch in text)
        {
            sb.Append(ch);
            run = ch == ' ' ? 0 : run + 1;
            if (ch is '\\' or '/' or '.' or '-' or '_' or '(' or ')' || run >= 18) { sb.Append('​'); run = 0; }
        }
        return sb.ToString();
    }

    public static string Plural(long n, string one, string many) => n == 1 ? $"{n:N0} {one}" : $"{n:N0} {many}";
}
