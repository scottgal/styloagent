using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Styloagent.Core.Hooks;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;

namespace Styloagent.App.ViewModels;

/// <summary>
/// View-model that wraps one <see cref="AgentSession"/> and drives it through its
/// lifecycle (Unspawned → Live → Dehydrated → Live).  It IS a Dock <see cref="Document"/> so the
/// centre DockControl hosts it directly and renders it through the App.axaml DataTemplate
/// (AgentPaneViewModel → AgentPaneView) — the wrapper pattern (base Document + Context) does NOT
/// render its body in Dock 11.3. All extra properties are observable (CommunityToolkit.Mvvm).
/// </summary>
public sealed partial class AgentPaneViewModel : Document
{
    private static readonly TimeSpan DehydrateTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentSession _session;
    private readonly AgentManifestEntry _manifest;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionBrushHex))]
    private string _borderColorHex;

    /// <summary>Available per-terminal colour themes (for the pane's theme picker).</summary>
#pragma warning disable CA1822 // instance property so XAML can bind {Binding TerminalThemes}
    public IReadOnlyList<TerminalTheme> TerminalThemes => TerminalTheme.All;
#pragma warning restore CA1822

    /// <summary>The colour theme applied to this agent's terminal.</summary>
    [ObservableProperty]
    private TerminalTheme _selectedTerminalTheme = TerminalTheme.Default;

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

    /// <summary>When this agent entered WaitingForHuman (null when it is not waiting). Drives queue order.</summary>
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>
    /// Called by the hosting view when the user interacts with this pane's terminal.
    /// Wired by <see cref="MainWindowViewModel"/> to <c>InteractionMonitor.RecordInput</c>
    /// so auto-reveal is suppressed while the human is actively typing.
    /// </summary>
    public Action? UserInteracted { get; set; }

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

    /// <summary>The agent's prefix (e.g. "foss-"), read from the manifest entry.</summary>
    public string Prefix => _manifest.Prefix;

    /// <summary>Prefix of the parent agent, or null for root-level panes.</summary>
    public string? ParentPrefix { get; init; }

    /// <summary>Nesting depth: 0 for root panes (overview, manually-added), 1+ for spawned children.</summary>
    public int Depth { get; init; }

    /// <summary>Human-readable responsibility description for this agent.</summary>
    public string Responsibility { get; init; } = "";

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

        // Dock document identity: Id is the (unique) prefix; Title is the tab caption.
        Id = manifest.Prefix;
        Title = displayName;
        CanFloat = true;
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

    /// <summary>Renames the display name shown in the cockpit UI (and the dock tab title).</summary>
    [RelayCommand]
    public void Rename(string newName)
    {
        DisplayName = newName;
        Title = newName;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> ReadPromptOrDefaultAsync(
        string path, string defaultPrompt, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return await File.ReadAllTextAsync(path, ct);
        return defaultPrompt;
    }
}
