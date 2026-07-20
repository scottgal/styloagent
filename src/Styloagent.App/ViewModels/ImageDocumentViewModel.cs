using Avalonia.Media.Imaging;
using Dock.Model.Mvvm.Controls;

namespace Styloagent.App.ViewModels;

/// <summary>A local raster image opened on the document surface.</summary>
public sealed class ImageDocumentViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    public bool DeferContentPresentation => false;
    public string FullPath { get; }
    public Bitmap? Bitmap { get; }
    public bool IsValid => Bitmap is not null;

    public ImageDocumentViewModel(string title, string fullPath)
    {
        Id = title;
        Title = title;
        FullPath = fullPath;
        try { Bitmap = File.Exists(fullPath) ? new Bitmap(fullPath) : null; }
        catch { Bitmap = null; }
    }
}
