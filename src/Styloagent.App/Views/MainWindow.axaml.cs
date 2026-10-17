using System.IO;
using Avalonia;
using Avalonia.Controls;
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

            // Confirm over this window, then close through the awaited agent teardown below.
            vm.ConfirmShutdownAsync = message => new ConfirmDialog(message).ShowDialog<bool>(this);
            // Roster reparent (drag-drop): confirm the consequential authority-tree edit over this window.
            vm.ConfirmReparentAsync = message => new ConfirmDialog(message, "Move").ShowDialog<bool>(this);
            vm.RequestAgentNameAsync = currentName => new RenameDialog(currentName).ShowDialog<string?>(this);
            // Close routes through the awaited teardown below; desktop.Shutdown() forces closing
            // and bypasses cancellation, so it can exit before owned agents have stopped.
            vm.RequestShutdown = Close;
        };
    }

    private bool _closingAgents;
    private bool _agentsStopped;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_agentsStopped && DataContext is MainWindowViewModel vm)
        {
            e.Cancel = true;
            if (!_closingAgents)
            {
                _closingAgents = true;
                _ = StopAgentsAndCloseAsync(vm);
            }
        }
        base.OnClosing(e);
    }

    private async Task StopAgentsAndCloseAsync(MainWindowViewModel vm)
    {
        // Even a synchronous fake session must not recursively Close inside OnClosing.
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        try
        {
            await vm.StopAsync();
            vm.Dispose();
            _agentsStopped = true;
            Close();
        }
        catch (Exception ex)
        {
            _closingAgents = false;
            System.Diagnostics.Trace.WriteLine($"[Styloagent] Could not stop cockpit: {ex}");
        }
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
