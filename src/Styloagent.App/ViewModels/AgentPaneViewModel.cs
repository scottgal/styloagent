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
    [NotifyPropertyChangedFor(nameof(WaitingTooltip))]
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
    [NotifyPropertyChangedFor(nameof(WaitingTooltip))]
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
    /// The human-readable question this agent is blocked on — the <see cref="HookEvent.Message"/> from a
    /// permission_prompt / agent_needs_input Notification. Surfaced as the roster row tooltip so the operator
    /// sees WHAT is being asked without hunting for the terminal pane. Empty when the agent isn't waiting.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaitingQuestion))]
    [NotifyPropertyChangedFor(nameof(WaitingTooltip))]
    private string _waitingQuestion = "";

    /// <summary>True when a pending question is available to show in the tooltip.</summary>
    public bool HasWaitingQuestion => !string.IsNullOrEmpty(WaitingQuestion);

    /// <summary>
    /// Row tooltip: the pending question when the agent is blocked on you; a hint if it's waiting but the
    /// question wasn't captured; otherwise the plain live status — so a hover is always accurate, never a
    /// stale "waiting" on a working agent.
    /// </summary>
    public string WaitingTooltip => HasWaitingQuestion
        ? WaitingQuestion
        : NeedsYou ? "Waiting for you — open the terminal to answer."
        : StatusHeadline;

    /// <summary>
    /// The raw "human touched this terminal" callback the host wires to
    /// <c>InteractionMonitor.RecordInput</c> so auto-reveal is suppressed while the human is actively
    /// typing. Kept separate from <see cref="UserInteracted"/> so the pane can layer its own optimistic
    /// badge-clear on top without every wiring site having to compose it.
    /// </summary>
    public Action? InteractionRecorder { get; set; }

    private Action? _userInteracted;

    /// <summary>
    /// Called by the hosting view when the user interacts with this pane's terminal. Does two things in
    /// order: (1) optimistically clears a stale ⚠ "needs you" badge — the operator answering in-terminal
    /// IS the answer, so the roster flips to "working" the instant they type rather than lingering amber
    /// until the next hook event lands; (2) forwards to <see cref="InteractionRecorder"/> for idle-gating.
    /// A get-only cached delegate so the session-owned view can keep invoking it as an <see cref="Action"/>.
    /// </summary>
    public Action UserInteracted => _userInteracted ??= HandleUserInteraction;

    private void HandleUserInteraction()
    {
        if (NoteTerminalInteraction()) Host?.RefreshAttention();
        InteractionRecorder?.Invoke();
    }

    /// <summary>
    /// Optimistically advances a <see cref="AgentHookState.WaitingForHuman"/> pane to
    /// <see cref="AgentHookState.Working"/> when the operator interacts with its terminal (answering the
    /// pending prompt), clearing the waiting metadata so the roster badge updates immediately instead of
    /// waiting on the next Claude Code hook event — which can lag seconds behind a slow approved tool.
    /// Real hook events then keep the state honest. Returns true when the state actually changed, so the
    /// host can refresh the fleet attention HUD.
    /// </summary>
    public bool NoteTerminalInteraction()
    {
        if (HookState != AgentHookState.WaitingForHuman) return false;
        HookState = AgentHookState.Working;
        WaitingSince = null;
        WaitingQuestion = "";
        return true;
    }

    /// <summary>
    /// Opens THIS agent's durable log (<c>.styloagent/logs/&lt;prefix&gt;.md</c>) in the rendered-markdown
    /// viewer — the "Log (this agent)" entry in the pane chrome dropdown (agent-log design, slice 3).
    /// Routes through the host, which resolves the path against the active project.
    /// </summary>
    [RelayCommand]
    private void OpenLog() => Host?.OpenAgentLog(Prefix);

    // ── Pending operator question (ask_operator top bar) ─────────────────────────────────────

    /// <summary>
    /// The question this agent has raised to the human operator via <c>ask_operator</c> (empty when none).
    /// Reconciled by the shell from the OperatorQuestionHub's pending set. Deliberately SEPARATE from
    /// <see cref="HookState"/>: ask_operator is non-blocking (the asker keeps working / goes idle), so forcing
    /// WaitingForHuman would poison the hook-driven state the delivery service reads and make it defer the
    /// very answer meant to reach the asker. This is a "asked-and-continuing" marker, not a blocked state.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperatorQuestion))]
    [NotifyPropertyChangedFor(nameof(OperatorQuestionTooltip))]
    private string _pendingOperatorQuestion = "";

    /// <summary>True while this agent is waiting on an operator answer — drives the roster "asked you" badge
    /// (glyph "❓" / cyan #4DB8C4, distinct from the amber ⚠ needs-you highlight — rendered in the view).</summary>
    public bool HasOperatorQuestion => !string.IsNullOrEmpty(PendingOperatorQuestion);

    /// <summary>Tooltip: the pending question, so the operator sees what's asked from the roster.</summary>
    public string OperatorQuestionTooltip => HasOperatorQuestion ? PendingOperatorQuestion : "";

    /// <summary>
    /// The hosting <see cref="MainWindowViewModel"/>, so the per-agent management menu — rendered in a Flyout
    /// popup, OUTSIDE this row's visual tree — can bind to fleet-level commands (Kill / Force-kill / Approve /
    /// Hide / Remove) via <c>Host.…Command</c>. A visual-ancestor binding can't cross a popup boundary; this can.
    /// </summary>
    public MainWindowViewModel? Host { get; set; }

    /// <summary>True once at least one hook event has arrived — gates the "last output" line.</summary>
    public bool HasActivityMeta => LastActivityAt is not null;

    /// <summary>
    /// Applies a hook event: advances this pane's <see cref="HookState"/>, stamps the
    /// "last output" time, and refreshes the activity detail from the tool the agent just ran.
    /// </summary>
    public void ApplyHookEvent(HookEvent e)
    {
        HookState = Runtime == AgentRuntimeKind.Codex && e.EventName == "Stop"
            ? AgentHookState.Idle
            : HookStateMachine.Next(HookState, e);
        LastActivityAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(LastOutputText));
        OnPropertyChanged(nameof(HasActivityMeta));

        if (!string.IsNullOrEmpty(e.SessionId)) _sessionId = e.SessionId;
        if (!string.IsNullOrEmpty(e.Cwd)) _cwd = e.Cwd;

        if (e.EventName is "PreToolUse" or "PostToolUse" && !string.IsNullOrEmpty(e.ToolName))
            ActivityDetail = HookActivity.DescribeTool(e.ToolName);
        else if (HookState is AgentHookState.Idle or AgentHookState.Exited)
            ActivityDetail = ""; // a stale "editing" on an idle agent would mislead

        // Capture the question the agent is blocked on (roster tooltip); clear it once it moves on.
        if ((e.EventName == "PermissionRequest"
                || (e.EventName == "Notification"
                    && e.NotificationType is "permission_prompt" or "agent_needs_input" or "elicitation_dialog"))
            && !string.IsNullOrWhiteSpace(e.Message))
            WaitingQuestion = e.Message!.Trim();
        else if (HookState != AgentHookState.WaitingForHuman)
            WaitingQuestion = "";
    }

    // ── Pane-chrome terminal zoom relay (0b) ─────────────────────────────────────────────────

    /// <summary>Zoom bounds — mirror session-'s TerminalControl coercion range (3d34c3e).</summary>
    private const double MinZoomValue = 0.5;
    private const double MaxZoomValue = 3.0;

    /// <summary>
    /// Terminal font zoom for this pane. The decoupling relay between the zoom Slider in the pane chrome
    /// (AgentPaneChromeView) and session-'s <c>TerminalControl.ZoomLevel</c> StyledProperty: both bind here
    /// two-way, so moving the slider zooms the terminal and Ctrl+MouseWheel moves the slider back. 1.0 = the
    /// app-wide base font; coerced to [0.5, 3.0] (the TerminalControl re-coerces too, so an out-of-range
    /// binding can never drive it wild).
    /// </summary>
    [ObservableProperty]
    private double _zoomLevel = 1.0;

    partial void OnZoomLevelChanged(double value)
    {
        var clamped = Math.Clamp(value, MinZoomValue, MaxZoomValue);
        if (clamped != value) ZoomLevel = clamped;   // converges: the clamped set is in range, so no re-clamp
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

    [ObservableProperty]
    private long _remainingTokens;

    [ObservableProperty]
    private double _remainingFraction;

    [ObservableProperty]
    private string _contextPressure = "unknown";

    /// <summary>True once a usage readout is available — gates the roster line.</summary>
    public bool HasUsage => !string.IsNullOrEmpty(UsageText);

    /// <summary>Latest context-window fill (0–1), for the scope-dilution nudge. 0 until known.</summary>
    public double ContextFraction { get; set; }

    /// <summary>Set once the dilution nudge has fired for this agent, so it isn't repeated every tick.</summary>
    public bool DilutionNudged { get; set; }

    /// <summary>Set once adaptive compact-output guidance has been sent for the current pressure episode.</summary>
    public bool AdaptiveBudgetNudged { get; set; }

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
            var usage = _manifest.Runtime == AgentRuntimeKind.Codex
                ? Styloagent.Core.Transcripts.CodexTranscriptReader.ReadLatestForSession(sid)
                : Styloagent.Core.Transcripts.TranscriptReader.ReadLatest(
                    Styloagent.Core.Transcripts.TranscriptReader.PathFor(cwd, sid));
            var text = usage is null ? "" : $"{FormatTokens(usage.RemainingTokens)} left · {usage.ContextFraction * 100:0}% used";
            var frac = usage?.ContextFraction ?? 0;
            var remaining = usage?.RemainingTokens ?? 0;
            var remainingFraction = usage?.RemainingFraction ?? 0;
            var pressure = Styloagent.Core.Sessions.ContextPressurePolicy.For(frac).ToString().ToLowerInvariant();
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                UsageText = text;
                ContextFraction = frac;
                RemainingTokens = remaining;
                RemainingFraction = remainingFraction;
                ContextPressure = pressure;
            });
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

    /// <summary>The agent's PTY output stream (raw chunks). The shell subscribes an ApiThrottleDetector to
    /// spot API-error / rate-limit episodes — Claude Code fires no hook when throttled, so the output is the
    /// only signal.</summary>
    public event Action<string>? Output
    {
        add    => _session.Output += value;
        remove => _session.Output -= value;
    }

    // ── API throttle / rate-limit (transient; NOT a hook state) ──────────────────────────────────

    /// <summary>True while this agent is in a detected API-error / rate-limit episode — it looks alive but is
    /// stalled. Drives the amber ⏳ roster badge so the operator reads it as throttled, not "working".</summary>
    [ObservableProperty]
    private bool _isThrottled;

    /// <summary>The signature that opened the current throttle episode (e.g. "429", "overloaded").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThrottleTooltip))]
    private string? _lastThrottleSignature;

    /// <summary>When the current throttle episode began (null when not throttled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThrottleTooltip))]
    private DateTimeOffset? _throttledSince;

    /// <summary>Tooltip for the throttle badge — the matched signature + how long it's been throttled.</summary>
    public string ThrottleTooltip
    {
        get
        {
            var sig = string.IsNullOrWhiteSpace(LastThrottleSignature) ? "rate-limited" : LastThrottleSignature;
            if (ThrottledSince is { } since)
            {
                var mins = (int)Math.Max(0, (DateTimeOffset.UtcNow - since).TotalMinutes);
                return $"throttled / rate-limited — {sig} (for {mins}m)";
            }
            return $"throttled / rate-limited — {sig}";
        }
    }

    /// <summary>The agent's prefix (e.g. "foss-"), read from the manifest entry.</summary>
    public string Prefix => _manifest.Prefix;

    /// <summary>The CLI/runtime backing this pane.</summary>
    public AgentRuntimeKind Runtime => _manifest.Runtime;

    /// <summary>The resolved runtime/model/effort selection shown in the agent roster.</summary>
    public string SelectedModel => string.IsNullOrWhiteSpace(_manifest.Model) ? "default" : _manifest.Model;
    public string SelectedEffort => string.IsNullOrWhiteSpace(_manifest.Effort) ? "default" : _manifest.Effort;

    public string AgentSelectionText
        => $"{Runtime.ToString().ToLowerInvariant()} · {SelectedModel} · effort {SelectedEffort}";

    /// <summary>Prefix of the parent (owner) agent, or null for root-level panes. Settable so a roster
    /// reparent can re-owner the agent (drag-drop v2a).</summary>
    [ObservableProperty]
    private string? _parentPrefix;

    /// <summary>Nesting depth: 0 for root panes (overview, manually-added), 1+ for spawned children.
    /// Observable so a reparent updates the roster indent live.</summary>
    [ObservableProperty]
    private int _depth;

    /// <summary>Human-readable responsibility description for this agent.</summary>
    public string Responsibility { get; init; } = "";

    /// <summary>The agent's dedicated git worktree checkout path, or null if it shares the repo.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>The agent's dedicated branch (agent/&lt;slug&gt;), or null if it shares the repo.</summary>
    public string? WorktreeBranch { get; set; }

    /// <summary>Compact git status badge text for the roster (e.g. "✓", "↑3 ↓1 ✎", "⚠ conflict", "").</summary>
    [ObservableProperty]
    private string _gitBadgeText = "";

    /// <summary>The repo-identifying colour band on this agent's DOCK TAB (the repo overview's hue, matching
    /// the roster's per-repo grouping colour) so mixed-repo tabs are distinguishable at a glance. Null/empty
    /// in a single-repo workspace → no band. Set by the shell's roster rebuild.</summary>
    [ObservableProperty]
    private string? _repoBandColorHex;

    /// <summary>Tooltip for the tab repo band — the repo's name.</summary>
    [ObservableProperty]
    private string? _repoBandTooltip;

    /// <summary>
    /// True when the operator has HIDDEN this agent — its pane is removed from the visible dock surface to
    /// free screen space, but the session/PTY keeps running (it still shows as working in the roster).
    /// Distinct from Dehydrate, which kills the PTY. Toggled from the roster; the shell drops/re-adds the
    /// dockable while the session is left untouched.
    /// </summary>
    [ObservableProperty]
    private bool _isHidden;

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

        // Force Exited when the PTY process ends. A hard kill / crash does NOT fire Claude's SessionEnd
        // hook, so the hook state would otherwise stay stuck (e.g. ⚠ needs-you) on a dead agent. Wire every
        // spawn/rehydrate, plus the current PTY if this pane is created for an already-live session.
        _session.PtyStarted += WirePtyExit;
        if (_session.CurrentPty is { } livePty) WirePtyExit(livePty);
    }

    // ── Session exit → force Exited (independent of the hook stream) ─────────────

    private IPtySession? _wiredPty;

    /// <summary>Subscribes the pane to a PTY's Exited signal (idempotent; drops any prior wiring first).</summary>
    private void WirePtyExit(IPtySession pty)
    {
        if (ReferenceEquals(_wiredPty, pty)) return;
        if (_wiredPty is not null) _wiredPty.Exited -= OnPtyExited;
        _wiredPty = pty;
        pty.Exited += OnPtyExited;
    }

    /// <summary>
    /// The PTY process ended (natural exit, crash, or hard kill). A hard kill does NOT fire Claude's
    /// SessionEnd hook, so we force <see cref="AgentHookState.Exited"/> here regardless of hook events —
    /// otherwise a killed tab stays stuck on its last state (e.g. ⚠ needs-you). PortaPtySession raises
    /// Exited on a background thread and suppresses it on a graceful dehydrate-dispose (it unsubscribes
    /// ProcessExited before Kill), so this fires only on a genuine exit — never on a dehydrate.
    /// </summary>
    private void OnPtyExited()
    {
        if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            HookState = AgentHookState.Exited;
        else
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => HookState = AgentHookState.Exited);
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
                DefaultLaunchPrompt(), ct);
            await _session.SpawnAsync(prompt, ct);
        }
        catch (OperationCanceledException)
        {
            Styloagent.Core.Sessions.SpawnDiag.Log($"AgentPaneViewModel.SpawnAsync CANCELLED prefix={_manifest.Prefix}");
        }
        catch (Exception ex)
        {
            // No silent failures: a spawn failure must be observable, not swallowed.
            Styloagent.Core.Sessions.SpawnDiag.Log($"AgentPaneViewModel.SpawnAsync THREW prefix={_manifest.Prefix}: {ex}");
            System.Diagnostics.Trace.WriteLine(
                $"[AgentPaneViewModel] spawn failed for '{_manifest.Prefix}': {ex}");
        }
        State = _session.State;
    }

    private string DefaultLaunchPrompt() => _manifest.Runtime == AgentRuntimeKind.Codex
        ? $"You are the '{_manifest.Prefix}' Styloagent workspace agent. Read .styloagent/PROTOCOL.md and your mission doc if present, check the fleet inbox, then carry out your assigned task."
        : $"You are agent '{_manifest.Prefix}'. Begin your work.";

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
    /// Kills the agent immediately (no checkpoint). <paramref name="force"/> SIGKILLs the OS process tree for a
    /// stuck PTY. A graceful dispose SUPPRESSES the PTY's Exited signal (it can't tell kill from dehydrate), so
    /// we force the roster badge to Exited here and clear any pending "needs you" question.
    /// </summary>
    public async Task KillAsync(bool force)
    {
        try { await _session.KillAsync(force); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[AgentPaneViewModel] kill failed for '{_manifest.Prefix}': {ex}");
        }
        State = _session.State;
        HookState = AgentHookState.Exited;
        WaitingQuestion = "";
        WaitingSince = null;
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
            // Fallback points the revived agent at its own checkpoint file (written on dehydrate) so it
            // resumes from where it left off; a configured restart prompt still wins.
            var fallback = string.IsNullOrWhiteSpace(_manifest.SavedContextPath)
                ? $"You are agent '{_manifest.Prefix}'. Reload your saved context and resume."
                : $"You are agent '{_manifest.Prefix}'. Read your saved context at {_manifest.SavedContextPath} and resume where you left off.";
            var prompt = await ReadPromptOrDefaultAsync(_manifest.RestartPromptPath, fallback, ct);
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
