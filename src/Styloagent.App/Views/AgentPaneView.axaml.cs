using Avalonia;
using Avalonia.Controls;
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
        // Unsubscribe from the old VM.
        if (_vm is not null)
            _vm.PtyStarted -= OnPtyStarted;

        _vm = DataContext as AgentPaneViewModel;

        if (_vm is null)
            return;

        // Subscribe to future PTY sessions.
        _vm.PtyStarted += OnPtyStarted;

        // Apply border colour from the VM.
        if (Color.TryParse(_vm.BorderColorHex, out var color))
            PaneBorder.BorderBrush = new SolidColorBrush(color);

        // If a session is already live (e.g. VM was created before this view),
        // attach immediately.
        if (_vm.CurrentPty is { } existing)
            AttachTerminal(existing);
    }

    private void OnPtyStarted(IPtySession pty)
    {
        // PtyStarted fires on whatever thread SpawnAsync completes on.
        // Marshal to the UI thread before touching Avalonia controls.
        Dispatcher.UIThread.Post(() => AttachTerminal(pty), DispatcherPriority.Normal);
    }

    private void AttachTerminal(IPtySession pty)
    {
        // Detach any previous session before attaching the new one.
        Terminal.Detach();
        Terminal.Attach(pty);
    }
}
