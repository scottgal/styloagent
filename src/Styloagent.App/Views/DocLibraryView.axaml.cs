using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Views;

/// <summary>
/// The Document Library tree. A file leaf is both clickable (opens the doc) and a DRAG SOURCE: dragging it
/// onto the centre document surface opens it there (via <see cref="MainWindow.DocPathFormat"/>). A plain
/// click still opens it — a drag only starts once the pointer moves past a small threshold.
/// </summary>
public partial class DocLibraryView : UserControl
{
    private DocLibraryViewModel.DocNode? _dragNode;
    private Point _pressPoint;

    public DocLibraryView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragNode = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // A file leaf carries a DocEntry; folders don't and aren't draggable.
        if ((e.Source as Control)?.DataContext is DocLibraryViewModel.DocNode { IsFolder: false, Entry: not null } node)
        {
            _dragNode = node;
            _pressPoint = e.GetPosition(this);
        }
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode?.Entry is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragNode = null; return; }

        var moved = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4) return;   // still a click, not yet a drag

        var path = _dragNode.Entry.FullPath;
        _dragNode = null;
        if (string.IsNullOrWhiteSpace(path)) return;

        var data = new DataObject();
        data.Set(MainWindow.DocPathFormat, path);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _dragNode = null;
}
