using System;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.Core.Sessions;

namespace Styloagent.App.Views;

/// <summary>
/// Code-behind for a plain console pane: attaches the <see cref="Styloagent.Terminal.TerminalControl"/>
/// to the shell PTY on the UI thread, mirroring <see cref="AgentPaneView"/> but without the agent
/// toolbar/theme wiring.
/// </summary>
public partial class ConsolePaneView : UserControl
{
    private ConsolePaneViewModel? _vm;

    public ConsolePaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PtyStarted -= OnPtyStarted;
        _vm = DataContext as ConsolePaneViewModel;
        if (_vm is null) return;

        _vm.PtyStarted += OnPtyStarted;
        if (_vm.CurrentPty is { } existing) Attach(existing);
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (_vm is not null) _vm.PtyStarted -= OnPtyStarted;
        Terminal.Detach();
        base.OnDetachedFromLogicalTree(e);
    }

    private void OnPtyStarted(IPtySession pty)
        => Dispatcher.UIThread.Post(() =>
        {
            try { Attach(pty); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ConsolePaneView] attach failed: {ex}"); }
        });

    private void Attach(IPtySession pty)
    {
        Terminal.Detach();
        Terminal.Attach(pty);
    }
}
