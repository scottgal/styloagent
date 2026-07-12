using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Styloagent.Core.Git;
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
public sealed partial class AgentPaneViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    // Present this document's content immediately instead of via Dock's Background-priority deferred
    // queue. A live agent terminal continuously renders, starving that low-priority queue so a newly
    // activated document's content would never materialise — the "docs don't open / spawn overflows"
    // bug. Bypassing deferral makes the DocumentControl swap content synchronously on activation.
    public bool DeferContentPresentation => false;

    private static readonly TimeSpan DehydrateTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentSession _session;
    private readonly AgentManifestEntry _manifest;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionBrushHex))]
    [NotifyPropertyChangedFor(nameof(TabSelectedForegroundHex))]
    private string _borderColorHex;

    /// <summary>
    /// Legible caption colour for a tab FILLED with this agent's identity colour: black on a bright
    /// accent, white on a dark one — so the active tab reads whatever the agent's colour is.
    /// </summary>
    public string TabSelectedForegroundHex => IsBrightColor(BorderColorHex) ? "#0A0A0A" : "#FFFFFF";

    /// <summary>Perceived-luminance test (ITU-R BT.601) on a <c>#RRGGBB</c> string; false if unparseable.</summary>
    private static bool IsBrightColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.TrimStart('#');
        if (h.Length < 6) return false;
        const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(h.AsSpan(0, 2), Hex, inv, out var r)) return false;
        if (!int.TryParse(h.AsSpan(2, 2), Hex, inv, out var g)) return false;
        if (!int.TryParse(h.AsSpan(4, 2), Hex, inv, out var b)) return false;
        return (0.299 * r + 0.587 * g + 0.114 * b) > 150;
    }

    /// <summary>Available per-terminal colour themes (for the pane's theme picker).</summary>
#pragma warning disable CA1822 // instance property so XAML can bind {Binding TerminalThemes}
    public IReadOnlyList<TerminalTheme> TerminalThemes => TerminalTheme.All;
#pragma warning restore CA1822

    /// <summary>The colour theme applied to this agent's terminal.</summary>
    [ObservableProperty]
    private TerminalTheme _selectedTerminalTheme = TerminalTheme.Default;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SpawnCommand))]
    [NotifyCanExecuteChangedFor(nameof(DehydrateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RehydrateCommand))]
    private SessionState _state;

    // Toolbar command guards: an agent can only Spawn from a non-live state, Dehydrate when live with a
    // checkpoint target, and Rehydrate when dehydrated. Keeps the buttons disabled in invalid states so
    // e.g. "Spawn" on an already-running agent can't orphan its process.
    private bool CanSpawn => State != SessionState.Live;
    private bool CanDehydrate => State == SessionState.Live && !string.IsNullOrWhiteSpace(_manifest.SavedContextPath);
    private bool CanRehydrate => State == SessionState.Dehydrated;

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
    [NotifyPropertyChangedFor(nameof(StatusHeadline))]
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

    /// <summary>
    /// The "what is it doing right now" phrase derived from the last tool the agent invoked
    /// (e.g. "reading files", "running commands"). Empty until a tool fires. Only surfaced while
    /// <see cref="AgentHookState.Working"/> — a stale detail on an idle agent would mislead.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusHeadline))]
    private string _activityDetail = "";

    /// <summary>
    /// Headline status line for the roster: the live activity detail while working
    /// ("indexing repo"), otherwise the plain state word ("idle", "needs you").
    /// </summary>
    public string StatusHeadline =>
        HookState == AgentHookState.Working && !string.IsNullOrEmpty(ActivityDetail)
            ? ActivityDetail
            : HookStateText;

    /// <summary>Wall-clock time of the most recent hook event from this agent (null before first).</summary>
    public DateTimeOffset? LastActivityAt { get; private set; }

    /// <summary>
    /// Relative "last output" readout for the roster ("last output 12s", "last output 3m").
    /// Recomputed on a shared 1-second tick via <see cref="TickRelativeTimes"/>.
    /// </summary>
    public string LastOutputText
    {
        get
        {
            if (LastActivityAt is not { } t) return "";
            var age = DateTimeOffset.UtcNow - t;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            string span = age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds}s"
                        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m"
                        : $"{(int)age.TotalHours}h";
            return $"last output {span}";
        }
    }

    /// <summary>Pokes the relative-time readouts so "last output Ns" ticks without a per-pane timer.</summary>
    public void TickRelativeTimes() => OnPropertyChanged(nameof(LastOutputText));

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

    /// <summary>True once at least one hook event has arrived — gates the "last output" line.</summary>
    public bool HasActivityMeta => LastActivityAt is not null;

    /// <summary>
    /// Applies a hook event: advances this pane's <see cref="HookState"/>, stamps the
    /// "last output" time, and refreshes the activity detail from the tool the agent just ran.
    /// </summary>
    public void ApplyHookEvent(HookEvent e)
    {
        HookState = HookStateMachine.Next(HookState, e);
        LastActivityAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(LastOutputText));
        OnPropertyChanged(nameof(HasActivityMeta));

        if (!string.IsNullOrEmpty(e.SessionId)) _sessionId = e.SessionId;
        if (!string.IsNullOrEmpty(e.Cwd)) _cwd = e.Cwd;

        if (e.EventName is "PreToolUse" or "PostToolUse" && !string.IsNullOrEmpty(e.ToolName))
            ActivityDetail = HookActivity.DescribeTool(e.ToolName);
        else if (HookState is AgentHookState.Idle or AgentHookState.Exited)
            ActivityDetail = ""; // a stale "editing" on an idle agent would mislead
    }

    // ── Token / context usage (read from the agent's Claude transcript) ──────────────────────
    private string? _sessionId;
    private string? _cwd;

    /// <summary>The agent's Claude transcript path (cwd + session id), or null before the first hook event.</summary>
    public string? TranscriptPath
        => Styloagent.Core.Transcripts.TranscriptReader.PathFor(_cwd ?? _manifest.Worktree, _sessionId);

    /// <summary>Compact token/context readout for the roster, e.g. "83k · 22%". Empty until known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    private string _usageText = "";

    /// <summary>True once a usage readout is available — gates the roster line.</summary>
    public bool HasUsage => !string.IsNullOrEmpty(UsageText);

    /// <summary>Latest context-window fill (0–1), for the scope-dilution nudge. 0 until known.</summary>
    public double ContextFraction { get; set; }

    /// <summary>Set once the dilution nudge has fired for this agent, so it isn't repeated every tick.</summary>
    public bool DilutionNudged { get; set; }

    /// <summary>
    /// Reads the agent's transcript (off the UI thread) for the latest context tokens + window fill and
    /// updates <see cref="UsageText"/>. No-op until a hook event has supplied the session id.
    /// </summary>
    public void RefreshUsage()
    {
        var cwd = _cwd ?? _manifest.Worktree;
        var sid = _sessionId;
        if (string.IsNullOrEmpty(sid)) return;

        _ = Task.Run(() =>
        {
            var path = Styloagent.Core.Transcripts.TranscriptReader.PathFor(cwd, sid);
            var usage = Styloagent.Core.Transcripts.TranscriptReader.ReadLatest(path);
            var text = usage is null ? "" : $"{FormatTokens(usage.ContextTokens)} · {usage.ContextFraction * 100:0}%";
            var frac = usage?.ContextFraction ?? 0;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => { UsageText = text; ContextFraction = frac; });
        });
    }

    private static string FormatTokens(long t) => t >= 1000 ? $"{t / 1000}k" : t.ToString();

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

    /// <summary>The agent's dedicated git worktree checkout path, or null if it shares the repo.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>The agent's dedicated branch (agent/&lt;slug&gt;), or null if it shares the repo.</summary>
    public string? WorktreeBranch { get; set; }

    /// <summary>Compact git status badge text for the roster (e.g. "✓", "↑3 ↓1 ✎", "⚠ conflict", "").</summary>
    [ObservableProperty]
    private string _gitBadgeText = "";

    /// <summary>Recomputes the git badge for this pane's worktree (no-op if it has none).</summary>
    public async Task RefreshGitStatusAsync(IGitService git)
    {
        if (WorktreePath is null) { GitBadgeText = ""; return; }
        var status = await git.GetStatusAsync(WorktreePath);
        GitBadgeText = GitBadge.Format(status.Ok ? status.Value : null, hasWorktree: true);
    }

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
    [RelayCommand(CanExecute = nameof(CanSpawn))]
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
    [RelayCommand(CanExecute = nameof(CanDehydrate))]
    public async Task DehydrateAsync(CancellationToken ct = default)
    {
        try { await _session.DehydrateAsync(DehydrateTimeout, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A toolbar click must never crash the app — surface, don't throw.
            System.Diagnostics.Trace.WriteLine($"[AgentPaneViewModel] dehydrate failed for '{_manifest.Prefix}': {ex}");
        }
        State = _session.State;
    }

    /// <summary>
    /// Rehydrates a dehydrated session.  Reads the restart prompt from
    /// <see cref="AgentManifestEntry.RestartPromptPath"/> if the file exists;
    /// otherwise falls back to a minimal built-in brief.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRehydrate))]
    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        try
        {
            var prompt = await ReadPromptOrDefaultAsync(_manifest.RestartPromptPath,
                $"You are agent '{_manifest.Prefix}'. Reload your saved context and resume.", ct);
            await _session.RehydrateAsync(prompt, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[AgentPaneViewModel] rehydrate failed for '{_manifest.Prefix}': {ex}");
        }
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
