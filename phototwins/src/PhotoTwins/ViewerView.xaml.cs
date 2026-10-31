using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoTwins;

public partial class ViewerView : UserControl
{
    private double _zoom;            // 0 = fit the window; 1 = actual pixels
    private Point _dragFrom;
    private Point _dragOffset;
    private bool _dragging;
    private ViewerViewModel? _vm;

    public event Action? CloseRequested;

    public ViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ViewerViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.Image)) { _zoom = 0; ApplyZoom(); }
    }

    private double DipsPerPixel => 1.0 / Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);

    private (double w, double h)? PixelSize => Picture.Source is BitmapSource b ? (b.PixelWidth, b.PixelHeight) : null;

    private double FitFactor()
    {
        if (PixelSize is not { } p || Scroller.ViewportWidth <= 0) return 1;
        double vw = Math.Max(1, Scroller.ActualWidth - 2), vh = Math.Max(1, Scroller.ActualHeight - 2);
        return Math.Min(vw / (p.w * DipsPerPixel), vh / (p.h * DipsPerPixel));
    }

    /// <summary>Size the picture for the current zoom (fit the window, or a multiple of its real pixels).</summary>
    public void ApplyZoom()
    {
        if (PixelSize is not { } p) { Picture.Width = double.NaN; Picture.Height = double.NaN; return; }
        double fit = FitFactor();
        double z = _zoom <= 0 ? fit : _zoom;
        Picture.Width = p.w * DipsPerPixel * z;
        Picture.Height = p.h * DipsPerPixel * z;
        Scroller.HorizontalScrollBarVisibility = z > fit + 0.001 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Scroller.VerticalScrollBarVisibility = z > fit + 0.001 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    public void ZoomBy(double factor)
    {
        double fit = FitFactor();
        double current = _zoom <= 0 ? fit : _zoom;
        double next = Math.Clamp(current * factor, fit, 8.0);
        _zoom = next <= fit + 0.001 ? 0 : next;
        ApplyZoom();
    }

    public void ZoomToFit() { _zoom = 0; ApplyZoom(); }
    public void ZoomActual() { _zoom = _zoom == 1.0 ? 0 : 1.0; ApplyZoom(); }

    private void OnViewportChanged(object sender, SizeChangedEventArgs e) => ApplyZoom();

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomBy(Math.Pow(1.15, e.Delta / 120.0));
        e.Handled = true;
    }

    private void OnPictureMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ZoomActual(); return; }
        _dragging = true;
        _dragFrom = e.GetPosition(Scroller);
        _dragOffset = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        Picture.CaptureMouse();
    }

    private void OnPictureMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(_dragOffset.X - (now.X - _dragFrom.X));
        Scroller.ScrollToVerticalOffset(_dragOffset.Y - (now.Y - _dragFrom.Y));
    }

    private void OnPictureMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Picture.ReleaseMouseCapture();
    }

    private void OnFit(object sender, RoutedEventArgs e) => ZoomToFit();
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>Keyboard shortcuts for the viewer. Returns true if the key was used.</summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Escape: CloseRequested?.Invoke(); return true;
            case Key.Left: _vm?.PreviousCommand.Execute(null); return true;
            case Key.Right: _vm?.NextCommand.Execute(null); return true;
            case Key.Add or Key.OemPlus: ZoomBy(1.25); return true;
            case Key.Subtract or Key.OemMinus: ZoomBy(0.8); return true;
            case Key.D0 or Key.NumPad0: ZoomToFit(); return true;
            default: return false;
        }
    }
}
