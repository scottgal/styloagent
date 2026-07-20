using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Views;

/// <summary>Image viewer with fit-to-window, buttons, and Ctrl+wheel zoom.</summary>
public partial class ImageDocumentView : UserControl
{
    private double _zoom = 1;

    public ImageDocumentView()
    {
        InitializeComponent();
        _zoom = 0;
        PointerWheelChanged += OnWheel;
        SizeChanged += (_, _) => { if (_zoom == 0) Fit(); };
        DataContextChanged += (_, _) => Dispatcher.UIThread.Post(Fit);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != KeyModifiers.Control) return;
        SetZoom(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    private void ZoomIn(object? sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void ZoomOut(object? sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    private void FitImage(object? sender, RoutedEventArgs e) => Fit();

    private void Fit()
    {
        if (Preview.Source is not Avalonia.Media.Imaging.Bitmap bitmap || ImageScroll.Bounds.Width <= 0 || ImageScroll.Bounds.Height <= 0)
            return;
        var availableWidth = Math.Max(1, ImageScroll.Bounds.Width - 48);
        var availableHeight = Math.Max(1, ImageScroll.Bounds.Height - 48);
        SetZoom(Math.Min(1, Math.Min(availableWidth / bitmap.PixelSize.Width, availableHeight / bitmap.PixelSize.Height)));
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.05, 8);
        Preview.RenderTransform = new ScaleTransform(_zoom, _zoom);
        ZoomLabel.Text = $"{_zoom:P0}";
    }
}
