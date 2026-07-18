using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Views;

/// <summary>
/// The top-level Styloagent Dock window.
/// Hosts a 3-column shell: left bus placeholder | agent pane | right bus placeholder.
/// The centre document surface accepts drops (an in-app doc path, or OS files) and opens each in the
/// viewer that matches its type — the drag half of the doc-surface viewer-by-type dispatch.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>In-app drag payload: the absolute path of a document to open on the doc surface.</summary>
    public const string DocPathFormat = "styloagent/doc-path";

    public MainWindow()
    {
        InitializeComponent();
        DocumentSurface.AddHandler(DragDrop.DragOverEvent, OnSurfaceDragOver);
        DocumentSurface.AddHandler(DragDrop.DropEvent, OnSurfaceDrop);
        // Give the shell VM a real folder picker over this window for the open-repo gesture.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.RepoFolderPicker = new StorageFolderPicker(this);

            // Graceful shut down: confirm over this window, then trigger the app's graceful close (the
            // ShutdownRequested handler disposes the VM/watchers) — never Environment.Exit.
            vm.ConfirmShutdownAsync = message => new ConfirmDialog(message).ShowDialog<bool>(this);
            vm.RequestShutdown = () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else
                    Close();
            };
        };
    }

    private static bool HasOpenableData(IDataObject data)
        => data.Contains(DocPathFormat) || data.Contains(DataFormats.Files);

    private void OnSurfaceDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasOpenableData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSurfaceDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // In-app drag (e.g. a Document Library file) carries the path directly.
        if (e.Data.Get(DocPathFormat) is string path && !string.IsNullOrWhiteSpace(path))
        {
            vm.OpenDocumentByPath(path);
            e.Handled = true;
            return;
        }

        // OS file drop (drag a file in from Finder/Explorer).
        var files = e.Data.GetFiles();
        if (files is not null)
        {
            foreach (var item in files)
            {
                var local = item.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
                    vm.OpenDocumentByPath(local);
            }
            e.Handled = true;
        }
    }
}
