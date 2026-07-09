using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Hooks;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.App.ViewModels;

/// <summary>
/// View-model that wraps one <see cref="AgentSession"/> and drives it through its
/// lifecycle (Unspawned → Live → Dehydrated → Live).  All public properties are
/// observable via INotifyPropertyChanged (CommunityToolkit.Mvvm).
/// </summary>
public sealed partial class AgentPaneViewModel : ObservableObject
{
    private static readonly TimeSpan DehydrateTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentSession _session;
    private readonly AgentManifestEntry _manifest;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionBrushHex))]
    private string _borderColorHex;

    [ObservableProperty]
    private SessionState _state;

    /// <summary>
    /// True when this pane is the one whose terminal is currently shown. Managed by
    /// <see cref="MainWindowViewModel"/> so the roster can outline the active agent.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionBrushHex))]
    private bool _isSelected;

    /// <summary>Outline colour for the roster row: the agent's identity colour when selected, else transparent.</summary>
    public string SelectionBrushHex => IsSelected ? BorderColorHex : "#00000000";

    /// <summary>
    /// Live state derived from the agent's Claude Code hook stream (§4.4).
    /// Drives the roster badge — most importantly the ⚠ "waiting-for-human" highlight.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HookStateText))]
    [NotifyPropertyChangedFor(nameof(HookStateGlyph))]
    [NotifyPropertyChangedFor(nameof(HookStateColorHex))]
    [NotifyPropertyChangedFor(nameof(RowHighlightHex))]
    [NotifyPropertyChangedFor(nameof(NeedsYou))]
    private AgentHookState _hookState = AgentHookState.Unknown;

    /// <summary>Short human label for the current hook state, shown in the roster.</summary>
    public string HookStateText => HookState switch
    {
        AgentHookState.Working         => "working",
        AgentHookState.Idle            => "idle",
        AgentHookState.WaitingForHuman => "needs you",
        AgentHookState.Exited          => "exited",
        _                              => "—",
    };

    /// <summary>Glyph badge for the current hook state.</summary>
    public string HookStateGlyph => HookState switch
    {
        AgentHookState.Working         => "●",
        AgentHookState.Idle            => "○",
        AgentHookState.WaitingForHuman => "⚠",
        AgentHookState.Exited          => "✕",
        _                              => "·",
    };

    /// <summary>Colour for the state badge text/glyph, keyed by state.</summary>
    public string HookStateColorHex => HookState switch
    {
        AgentHookState.Working         => "#57A64A", // green
        AgentHookState.Idle            => "#8888AA", // gray
        AgentHookState.WaitingForHuman => "#FFCC33", // amber — needs you
        AgentHookState.Exited          => "#C05555", // red
        _                              => "#555577", // unknown
    };

    /// <summary>Row background — subtly highlighted amber when the agent needs you.</summary>
    public string RowHighlightHex => NeedsYou ? "#3A2E00" : "#111122";

    /// <summary>True when the agent is blocked on a human — the glanceable "who needs me".</summary>
    public bool NeedsYou => HookState == AgentHookState.WaitingForHuman;

    /// <summary>Applies a hook event, advancing this pane's <see cref="HookState"/>.</summary>
    public void ApplyHookEvent(HookEvent e) => HookState = HookStateMachine.Next(HookState, e);

    /// <summary>
    /// Raised when the underlying session starts a new PTY.
    /// The view subscribes to wire the TerminalControl.
    /// </summary>
    public event Action<IPtySession>? PtyStarted
    {
        add    => _session.PtyStarted += value;
        remove => _session.PtyStarted -= value;
    }

    /// <summary>
    /// The active PTY session, or null when the session is unspawned / dehydrated.
    /// Useful for initial wiring when the view is created after a session is already live.
    /// </summary>
    public IPtySession? CurrentPty => _session.CurrentPty;

    public AgentPaneViewModel(
        AgentSession session,
        AgentManifestEntry manifest,
        string displayName,
        string borderColorHex)
    {
        _session = session;
        _manifest = manifest;
        _displayName = displayName;
        _borderColorHex = borderColorHex;
        _state = session.State;
    }

    /// <summary>
    /// Spawns the agent session.  Reads the launch prompt from
    /// <see cref="AgentManifestEntry.LaunchPromptPath"/> if the file exists;
    /// otherwise falls back to a minimal built-in brief.
    /// </summary>
    [RelayCommand]
    public async Task SpawnAsync(CancellationToken ct = default)
    {
        try
        {
            var prompt = await ReadPromptOrDefaultAsync(_manifest.LaunchPromptPath,
                $"You are agent '{_manifest.Prefix}'. Begin your work.", ct);
            await _session.SpawnAsync(prompt, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // No silent failures: a spawn failure must be observable, not swallowed.
            System.Diagnostics.Trace.WriteLine(
                $"[AgentPaneViewModel] spawn failed for '{_manifest.Prefix}': {ex}");
        }
        State = _session.State;
    }

    /// <summary>
    /// Requests the session to dehydrate.  If the watcher does not ack in time
    /// (returns false) the session stays Live and <see cref="State"/> reflects that.
    /// </summary>
    [RelayCommand]
    public async Task DehydrateAsync(CancellationToken ct = default)
    {
        await _session.DehydrateAsync(DehydrateTimeout, ct);
        State = _session.State;
    }

    /// <summary>
    /// Rehydrates a dehydrated session.  Reads the restart prompt from
    /// <see cref="AgentManifestEntry.RestartPromptPath"/> if the file exists;
    /// otherwise falls back to a minimal built-in brief.
    /// </summary>
    [RelayCommand]
    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var prompt = await ReadPromptOrDefaultAsync(_manifest.RestartPromptPath,
            $"You are agent '{_manifest.Prefix}'. Reload your saved context and resume.", ct);
        await _session.RehydrateAsync(prompt, ct);
        State = _session.State;
    }

    /// <summary>Renames the display name shown in the cockpit UI.</summary>
    [RelayCommand]
    public void Rename(string newName) => DisplayName = newName;

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> ReadPromptOrDefaultAsync(
        string path, string defaultPrompt, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return await File.ReadAllTextAsync(path, ct);
        return defaultPrompt;
    }
}
