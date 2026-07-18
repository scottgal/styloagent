using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.Core.Sessions;

namespace Styloagent.App.Views;

/// <summary>
/// Code-behind for the agent pane view.
/// Subscribes to <see cref="AgentPaneViewModel.PtyStarted"/> to wire the
/// <see cref="Styloagent.Terminal.TerminalControl"/> on the UI thread.
/// </summary>
public partial class AgentPaneView : UserControl
{
    public AgentPaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private AgentPaneViewModel? _vm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from the old VM (same as Unloaded path — both are safe/idempotent).
        UnsubscribeVm();

        _vm = DataContext as AgentPaneViewModel;

        // (Re)wire the new VM: subscribe to its signals and attach any already-live PTY.
        WireVm();
    }

    /// <summary>
    /// BUG 4B fix: re-wire the terminal when this pane RE-ENTERS the logical tree. The centre dock
    /// recycles ONE view instance per agent (Dock ControlRecycling), keeping only the ACTIVE
    /// document's content in the tree; switching away runs <see cref="OnDetachedFromLogicalTree"/>
    /// (which <see cref="UnsubscribeVm"/>'s the PtyStarted subscription and detaches the terminal),
    /// so a federated pane whose PTY started while it was a background tab shows "just a cursor". The
    /// DataContext is unchanged across a tab-switch, so <see cref="OnDataContextChanged"/> does NOT
    /// fire again — this hook is what re-subscribes and re-attaches <c>CurrentPty</c>. Idempotent:
    /// <see cref="WireVm"/> unhooks before it subscribes and <see cref="AttachTerminal"/> detaches
    /// before it attaches, so the first attach and every re-attach both land exactly once.
    /// </summary>
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        WireVm();
    }

    /// <summary>
    /// Fix 4: unsubscribe + detach when the view is removed from the logical tree
    /// (window closed / pane removed) without a DataContext change, preventing handler leaks.
    /// </summary>
    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        UnsubscribeVm();
        Terminal.Detach();
        base.OnDetachedFromLogicalTree(e);
    }

    /// <summary>
    /// Idempotent VM wiring shared by the DataContext-change and logical-tree-attach paths: subscribe
    /// to PtyStarted + property changes + terminal interaction (unhooking first so a re-wire can't
    /// double-subscribe), apply the border/theme, and attach any already-live PTY. Called both when
    /// the DataContext is set and whenever a recycled pane re-enters the tree.
    /// </summary>
    private void WireVm()
    {
        if (_vm is null)
            return;

        // Unhook first — a re-wire (tab switched back to a recycled pane) must not stack handlers.
        _vm.PtyStarted -= OnPtyStarted;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        Terminal.UserInteracted -= OnTerminalUserInteracted;

        // Subscribe to future PTY sessions.
        _vm.PtyStarted += OnPtyStarted;

        // Forward terminal user interactions to the pane's attention callback.
        Terminal.UserInteracted += OnTerminalUserInteracted;

        // Apply border colour from the VM.
        if (Color.TryParse(_vm.BorderColorHex, out var color))
            PaneBorder.BorderBrush = new SolidColorBrush(color);

        // Apply + track this pane's terminal colour theme.
        _vm.PropertyChanged += OnVmPropertyChanged;
        Terminal.ApplyTheme(_vm.SelectedTerminalTheme);

        // If a session is already live (VM created before this view, or a PTY that started while this
        // recycled pane was a detached background tab), attach immediately so it paints on show.
        if (_vm.CurrentPty is { } existing)
            AttachTerminal(existing);
    }

    /// <summary>
    /// Shared unsubscribe helper used by both the DataContext-change and Unload paths.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    private void UnsubscribeVm()
    {
        if (_vm is not null)
        {
            _vm.PtyStarted -= OnPtyStarted;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        Terminal.UserInteracted -= OnTerminalUserInteracted;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentPaneViewModel.SelectedTerminalTheme) && _vm is not null)
            Terminal.ApplyTheme(_vm.SelectedTerminalTheme);
    }

    private void OnTerminalUserInteracted(object? sender, EventArgs e)
        => _vm?.UserInteracted?.Invoke();

    private void OnPtyStarted(IPtySession pty)
    {
        // PtyStarted fires on whatever thread SpawnAsync completes on.
        // Marshal to the UI thread before touching Avalonia controls.
        Dispatcher.UIThread.Post(() =>
        {
            // Fix 3: wrap in try/catch so an attach failure is never a silent black-hole.
            try
            {
                AttachTerminal(pty);
            }
            catch (Exception ex)
            {
                // TODO: route to a real error surface (status bar / error event) in a future PR.
                System.Diagnostics.Debug.WriteLine($"[AgentPaneView] attach failed: {ex}");
            }
        }, DispatcherPriority.Normal);
    }

    private void AttachTerminal(IPtySession pty)
    {
        // Fix 2: Detach() is idempotent (no-op when nothing is attached) — safe to call here
        // unconditionally so the first attach and subsequent re-attaches both work correctly.
        Terminal.Detach();
        Terminal.Attach(pty);
    }
}
