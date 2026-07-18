using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Views;

/// <summary>
/// The fleet roster. Drag one agent row onto another agent to REPARENT it — a within-repo authority-tree
/// edit (v2a). The drop is fully guarded by <see cref="MainWindowViewModel.ReparentAgentAsync"/> (the Core
/// authority lint + max depth + a confirm); an illegal move simply snaps back (nothing changes) with the
/// reason surfaced on the timeline. A plain click still selects the pane — only a drag past a small
/// threshold starts a reparent.
/// </summary>
public partial class AgentsView : UserControl
{
    private const string PaneFormat = "styloagent/agent-pane";
    private AgentPaneViewModel? _pressed;
    private Point _pressAt;

    public AgentsView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>The agent pane a hit element belongs to (DataContext inherits down the row); null for the
    /// repo-group headers.</summary>
    private static AgentPaneViewModel? PaneOf(object? source)
        => (source as StyledElement)?.DataContext as AgentPaneViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = PaneOf(e.Source);
        _pressAt = e.GetPosition(this);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressed = null; return; }

        var delta = e.GetPosition(this) - _pressAt;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;   // a click, not a drag

        var dragged = _pressed;
        _pressed = null;
        var data = new DataObject();
        data.Set(PaneFormat, dragged);
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        catch { /* a drag that fails to start must never crash the roster */ }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = PaneOf(e.Source);
        var dragged = e.Data.Get(PaneFormat) as AgentPaneViewModel;
        e.DragEffects = target is not null && dragged is not null && !ReferenceEquals(target, dragged)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = PaneOf(e.Source);
        var dragged = e.Data.Get(PaneFormat) as AgentPaneViewModel;
        if (target is null || dragged is null || DataContext is not MainWindowViewModel vm) return;

        var result = await vm.ReparentAgentAsync(dragged, target);
        // Snap-back is implicit (a rejected/cancelled move changes nothing); surface WHY on the timeline.
        if (!result.Applied && !result.Cancelled && result.Reason is { } reason)
            vm.Timeline.Add(DateTimeOffset.Now, "roster", "reparent rejected — " + reason, "#E5A05A");
    }
}
