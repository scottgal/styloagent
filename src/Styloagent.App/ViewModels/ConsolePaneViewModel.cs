using Dock.Model.Mvvm.Controls;
using Styloagent.Core.Sessions;

namespace Styloagent.App.ViewModels;

/// <summary>
/// A plain shell terminal hosted as a Dock document — NOT an agent (no claude, no hooks, no
/// spawn/dehydrate lifecycle). It just launches the user's default shell in a PTY. Rendered via the
/// App.axaml DataTemplate (ConsolePaneViewModel → ConsolePaneView), whose TerminalControl attaches to
/// <see cref="CurrentPty"/>.
/// </summary>
public sealed class ConsolePaneViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    // Present immediately — a live terminal starves Dock's Background-priority deferred queue (same
    // rationale as AgentPaneViewModel).
    public bool DeferContentPresentation => false;

    private IPtySession? _pty;

    /// <summary>The live shell PTY, or null before <see cref="StartAsync"/> completes.</summary>
    public IPtySession? CurrentPty => _pty;

    /// <summary>Raised when the shell PTY starts; the view attaches its TerminalControl.</summary>
    public event Action<IPtySession>? PtyStarted;

    public ConsolePaneViewModel(string id, string title)
    {
        Id = id;
        Title = title;
        CanFloat = true;
    }

    /// <summary>Launches the user's default shell (<c>$SHELL</c>, or <c>/bin/zsh</c>) in <paramref name="cwd"/>.</summary>
    public async Task StartAsync(IPtyLauncher launcher, string cwd, CancellationToken ct = default)
    {
        if (_pty is not null) return;
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell)) shell = "/bin/zsh";
        _pty = await launcher.SpawnAsync(
            new PtySpawnOptions(shell, Array.Empty<string>(), cwd, null, 120, 30), ct);
        PtyStarted?.Invoke(_pty);
    }
}
