using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.Config;
using Styloagent.App.Dock;
using Styloagent.App.Mcp;
using Styloagent.App.Router;
using Styloagent.App.Services;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Attention;
using Styloagent.Core.Channel;
using Styloagent.Core.Config;
using Styloagent.Core.Git;
using Styloagent.Core.Hooks;
using Styloagent.Git;
using Styloagent.Core.Mcp;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;
using Styloagent.Core.Seeding;
using Styloagent.Core.Diagrams;
using Styloagent.Core.Sessions;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Root view-model for the main window.  On construction it seeds the channel,
/// loads presentation data, and exposes the first agent as <see cref="Pane"/>.
/// Supports adding additional agent panes at runtime via <see cref="AddAgentCommand"/>.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private AgentPaneViewModel? _pane;

    /// <summary>All open agent panes — bound by the shell's tab strip.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AgentPaneViewModel> Panes { get; } = new();

    /// <summary>The roster grouped by repo (BUG 3): each repo's agents nest under THEIR OWN overview,
    /// with a repo header shown in multi-repo workspaces. Rebuilt from <see cref="Panes"/> + the known
    /// repos on every roster/repo change. Single-repo workspaces render as one header-less group.</summary>
    public System.Collections.ObjectModel.ObservableCollection<RosterRepoGroup> RosterGroups { get; } = new();

    /// <summary>Panes currently waiting for human attention, oldest-first.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AgentPaneViewModel> AttentionQueue { get; } = new();

    /// <summary>Count of panes currently waiting for human attention.</summary>
    public int WaitingCount => AttentionQueue.Count;

    /// <summary>HUD text for the attention queue: empty when nobody is waiting.</summary>
    public string AttentionHudText => WaitingCount == 0 ? "" : $"⚠ {WaitingCount} waiting";

    // ── Cockpit instruments (bottom status strip) ────────────────────────────
    /// <summary>Live agents in the fleet.</summary>
    public int LiveAgentCount => Panes.Count;
    /// <summary>Agents currently working (per their hook state).</summary>
    public int WorkingCount => Panes.Count(p => p.HookState == Styloagent.Core.Hooks.AgentHookState.Working);
    /// <summary>Agents currently idle.</summary>
    public int IdleCount => Panes.Count(p => p.HookState == Styloagent.Core.Hooks.AgentHookState.Idle);
    /// <summary>Operations recorded on the activity timeline.</summary>
    public int TimelineCount => Timeline.Entries.Count;

    /// <summary>Refreshes the instrument readouts (called whenever fleet/hook state changes).</summary>
    private void RefreshInstruments()
    {
        OnPropertyChanged(nameof(LiveAgentCount));
        OnPropertyChanged(nameof(WorkingCount));
        OnPropertyChanged(nameof(IdleCount));
        OnPropertyChanged(nameof(TimelineCount));
    }

    [ObservableProperty]
    private AgentPaneViewModel? _selectedPane;

    /// <summary>Collapse the left roster to give the centre terminals more width.</summary>
    [ObservableProperty]
    private bool _isRosterCollapsed;

    /// <summary>Collapse the right bus/docs panel.</summary>
    [ObservableProperty]
    private bool _isSidePanelCollapsed;

    /// <summary>Terminal themes offered in the settings picker.</summary>
#pragma warning disable CA1822 // instance property for XAML binding
    public IReadOnlyList<Styloagent.Terminal.TerminalTheme> TerminalThemes => Styloagent.Terminal.TerminalTheme.All;
#pragma warning restore CA1822

    /// <summary>Global terminal theme — setting it re-themes every open terminal.</summary>
    [ObservableProperty]
    private Styloagent.Terminal.TerminalTheme _globalTerminalTheme = Styloagent.Terminal.TerminalTheme.Default;

    partial void OnGlobalTerminalThemeChanged(Styloagent.Terminal.TerminalTheme value)
    {
        foreach (var pane in Panes)
            pane.SelectedTerminalTheme = value;
        SavePreferences();
    }

    /// <summary>App-wide light/dark toggle — swaps the structural theme tokens (Fluent variant).</summary>
    [ObservableProperty]
    private bool _isLightTheme;

    partial void OnIsLightThemeChanged(bool value)
    {
        if (Avalonia.Application.Current is { } app)
            ThemeApplier.ApplyThemeVariant(app, value);
        SavePreferences();
    }

    /// <summary>Accent presets offered in the settings picker.</summary>
#pragma warning disable CA1822 // instance property for XAML binding
    public IReadOnlyList<AccentPreset> AvailableAccents => AccentPalette.All;
#pragma warning restore CA1822

    /// <summary>The selected accent preset — setting it repaints the accent brushes app-wide.</summary>
    [ObservableProperty]
    private AccentPreset _selectedAccent = AccentPalette.Resolve(AccentPalette.DefaultName);

    partial void OnSelectedAccentChanged(AccentPreset value)
    {
        if (Avalonia.Application.Current is { } app)
            ThemeApplier.ApplyAccent(app, value);
        SavePreferences();
    }

    /// <summary>App-wide terminal font size (points). Persisted; applied to every live terminal.</summary>
    [ObservableProperty]
    private double _terminalFontSize = 13;

    partial void OnTerminalFontSizeChanged(double value)
    {
        Styloagent.Terminal.TerminalControl.SetGlobalFontSize(value);
        SavePreferences();
    }

    /// <summary>App-wide markdown / document font size (points). Persisted.</summary>
    [ObservableProperty]
    private double _markdownFontSize = 14;

    partial void OnMarkdownFontSizeChanged(double value) => SavePreferences();

    /// <summary>
    /// Whether the UI-automation surface (the MCP <c>screenshot</c> tool + the top-bar shot button) is
    /// enabled. Off by default — a privileged introspection surface. Turning it on broadcasts a bus
    /// notice so the fleet knows the cockpit can be observed.
    /// </summary>
    [ObservableProperty]
    private bool _uiAutomationEnabled;

    partial void OnUiAutomationEnabledChanged(bool value)
    {
        if (_prefsLoaded && value)
            _ = SendBusMessage(new MessageRequest(
                "cockpit-", "all-", "UI automation enabled",
                "The cockpit UI-automation surface is now enabled — agents may request screenshots via " +
                "the styloagent MCP `screenshot` tool.", "info"));
        SavePreferences();
    }

    /// <summary>Command behind the top-bar screenshot button (only shown when automation is enabled).</summary>
    [RelayCommand]
    private async Task CaptureScreenshot() => await CaptureScreenshotToFileAsync(null);

    /// <summary>
    /// Captures the cockpit window (or, unimplemented for now, a named control) to a timestamped PNG
    /// under the project's <c>.styloagent/shots/</c> and returns the path. Gated on
    /// <see cref="UiAutomationEnabled"/>. Shared by the top-bar button and the MCP tool.
    /// </summary>
    public async Task<string> CaptureScreenshotToFileAsync(string? target)
    {
        if (!UiAutomationEnabled) return "rejected: UI automation is disabled (enable it in Settings)";

        var window = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null) return "rejected: no cockpit window";

        try
        {
            var dir = _project is not null
                ? Path.Combine(_project.Root, ".styloagent", "shots")
                : Path.Combine(Path.GetTempPath(), "styloagent-shots");
            Directory.CreateDirectory(dir);
            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(dir, $"cockpit-{stamp}.png");
            await Mostlylucid.Avalonia.UITesting.Players.ScreenshotCapture.CaptureWindowAsync(window, path);
            return path;
        }
        catch (Exception ex)
        {
            return $"rejected: {ex.Message}";
        }
    }

    // ── Persistence of the above (accent, theme, terminal theme, font sizes) ──────────────────
    private PreferencesStore? _prefsStore;
    private string? _prefsPath;
    private AppPreferences _prefs = new();
    private bool _prefsLoaded;   // gate: don't save while seeding from disk

    /// <summary>
    /// Seeds the observable settings from persisted <paramref name="prefs"/> and enables save-on-change.
    /// Accent + theme variant were already applied at startup (App.axaml.cs); this aligns the VM's
    /// bound state with them so the pickers show the right selection.
    /// </summary>
    public void AttachPreferences(AppPreferences prefs, PreferencesStore store, string path)
    {
        _prefs = prefs;
        _prefsStore = store;
        _prefsPath = path;

        IsLightTheme = prefs.LightTheme;
        SelectedAccent = AccentPalette.Resolve(prefs.Accent);
        var theme = TerminalThemes.FirstOrDefault(t =>
            string.Equals(t.Name, prefs.TerminalTheme, StringComparison.OrdinalIgnoreCase));
        if (theme is not null) GlobalTerminalTheme = theme;
        TerminalFontSize = prefs.TerminalFontSize;
        MarkdownFontSize = prefs.MarkdownFontSize;
        UiAutomationEnabled = prefs.EnableUiAutomation;
        SelectedPermissionMode = Enum.TryParse<Styloagent.Core.Hooks.FleetPermissionMode>(prefs.PermissionMode, out var pm)
            ? pm : Styloagent.Core.Hooks.FleetPermissionMode.Scoped;
        Styloagent.Terminal.TerminalControl.SetGlobalFontSize(TerminalFontSize);

        _prefsLoaded = true;   // seeding complete — subsequent changes persist
    }

    /// <summary>Snapshots the current settings into the prefs file (best-effort, fire-and-forget).</summary>
    private void SavePreferences()
    {
        if (!_prefsLoaded || _prefsStore is null || _prefsPath is null) return;
        _prefs.LightTheme = IsLightTheme;
        _prefs.Accent = SelectedAccent.Name;
        _prefs.TerminalTheme = GlobalTerminalTheme.Name;
        _prefs.TerminalFontSize = TerminalFontSize;
        _prefs.MarkdownFontSize = MarkdownFontSize;
        _prefs.EnableUiAutomation = UiAutomationEnabled;
        _prefs.PermissionMode = SelectedPermissionMode.ToString();
        _ = _prefsStore.SaveAsync(_prefsPath, _prefs);
    }

    /// <summary>The permission modes offered in Settings (bound to the picker).</summary>
    public IReadOnlyList<Styloagent.Core.Hooks.FleetPermissionMode> PermissionModes { get; } =
        new[] { Styloagent.Core.Hooks.FleetPermissionMode.Prompt, Styloagent.Core.Hooks.FleetPermissionMode.Scoped, Styloagent.Core.Hooks.FleetPermissionMode.Bypass };

    /// <summary>The chosen fleet permission mode — new agents launch with it; persisted. Drives <see cref="PermissionMode"/>.</summary>
    [ObservableProperty]
    private Styloagent.Core.Hooks.FleetPermissionMode _selectedPermissionMode = Styloagent.Core.Hooks.FleetPermissionMode.Scoped;

    partial void OnSelectedPermissionModeChanged(Styloagent.Core.Hooks.FleetPermissionMode value)
        => SavePreferences();   // new spawns read PermissionMode (=> SelectedPermissionMode)

    [ObservableProperty]
    private IRootDock? _layout;

    /// <summary>How the centre tiles the agent panes (top-bar segmented switch). Tabs by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabsLayout))]
    [NotifyPropertyChangedFor(nameof(IsTileLayout))]
    [NotifyPropertyChangedFor(nameof(IsAutoTileLayout))]
    private CockpitLayoutMode _layoutMode = CockpitLayoutMode.Tabs;

    public bool IsTabsLayout => LayoutMode == CockpitLayoutMode.Tabs;
    public bool IsTileLayout => LayoutMode == CockpitLayoutMode.Tile;
    public bool IsAutoTileLayout => LayoutMode == CockpitLayoutMode.AutoTile;

    /// <summary>Switches the centre layout mode (top-bar Tabs / Tile / Auto-tile) and rebuilds it.</summary>
    [RelayCommand]
    private void SetLayoutMode(string mode)
    {
        var m = mode switch
        {
            "Tile"     => CockpitLayoutMode.Tile,
            "AutoTile" => CockpitLayoutMode.AutoTile,
            _          => CockpitLayoutMode.Tabs,
        };
        if (m == LayoutMode && Layout is not null) return;
        LayoutMode = m;
        RebuildCenterLayout();
    }

    /// <summary>
    /// Rebuilds the centre dock tree for the current <see cref="LayoutMode"/> from the live panes,
    /// preserving which pane is active. The pane view-models are reused, so their terminals persist.
    /// </summary>
    private void RebuildCenterLayout()
    {
        if (_dockFactory is null) return;
        // Hidden agents are kept off the document surface (Fix F) — their sessions run on, but they don't
        // take a tile/tab, even across a layout-mode switch.
        var previous = Layout;
        var visible = Panes.Where(p => !p.IsHidden).ToList();
        var active = (SelectedPane is { IsHidden: false } sel ? sel : null) ?? visible.FirstOrDefault();
        var layout = _dockFactory.BuildLayout(visible, LayoutMode);
        Layout = layout;
        _dockFactory.InitLayout(layout);
        if (active is not null) _dockFactory.SetActiveDockable(active);
        // P0 memory-leak fix: BuildLayout makes a FRESH RootDock every switch, but nothing released the
        // OLD one — its container docks (+ the Avalonia AgentPaneView/TerminalControl subtrees materialized
        // under them) stayed rooted, so their scrollback + Skia/composition resources never freed (unbounded
        // growth per switch). Sever the previous tree so it becomes collectible; the reused pane VMs are
        // already re-homed in the new layout by InitLayout, so we only clear the discarded container docks.
        ReleaseDockTree(previous, keep: layout);
    }

    /// <summary>Detaches a discarded dock tree from itself so it (and every view Avalonia materialized under
    /// it) can be garbage-collected. Clears only CONTAINER docks — reused pane view-models, already re-homed
    /// in <paramref name="keep"/>, are left untouched. Part of the layout-switch memory-leak fix.</summary>
    private static void ReleaseDockTree(global::Dock.Model.Core.IDockable? old, global::Dock.Model.Core.IDock keep)
    {
        if (old is null || ReferenceEquals(old, keep)) return;

        static void Walk(global::Dock.Model.Core.IDockable node)
        {
            if (node is global::Dock.Model.Core.IDock dock)
            {
                if (dock.VisibleDockables is { } children)
                {
                    foreach (var child in children.ToList())
                        Walk(child);
                    children.Clear();
                }
                dock.ActiveDockable = null;
                dock.DefaultDockable = null;
                dock.FocusedDockable = null;
                dock.Owner = null;
            }
            // Non-dock dockables (the reused AgentPaneViewModel documents) are kept live — only unlink them
            // from the discarded tree if they still point back into it.
            else if (node is not AgentPaneViewModel)
            {
                node.Owner = null;
            }
        }

        Walk(old);
    }

    /// <summary>
    /// Fix F — HIDE a live agent: take its pane off the document surface to free screen space while its PTY
    /// keeps running (it stays in the roster, still working). Distinct from Dehydrate, which kills the PTY:
    /// the session is left untouched, only its dockable is removed. Restore with <see cref="ShowAgentCommand"/>.
    /// </summary>
    [RelayCommand]
    private void HideAgent(AgentPaneViewModel? pane)
    {
        if (pane is null || pane.IsHidden) return;
        pane.IsHidden = true;
        if (_dockFactory is not null && pane.Owner is global::Dock.Model.Core.IDock)
            _dockFactory.RemoveDockable(pane, collapse: true);   // drop the dockable; the session runs on
    }

    /// <summary>Fix F — RESTORE a hidden agent's pane to the document surface. No rehydrate: it never stopped.</summary>
    [RelayCommand]
    private void ShowAgent(AgentPaneViewModel? pane)
    {
        if (pane is null || !pane.IsHidden) return;
        pane.IsHidden = false;
        if (_dockFactory?.DocumentDock is { } dock)
        {
            if (!ReferenceEquals(pane.Owner, dock))
                _dockFactory.AddDockable(dock, pane);
            _dockFactory.SetActiveDockable(pane);
            if (_dockFactory.RootDock is { } root) _dockFactory.SetFocusedDockable(root, pane);
        }
    }

    /// <summary>
    /// Human-in-the-loop approval: sends "Yes" (option 1 + Enter) to a waiting agent's permission prompt over
    /// its PTY, so the operator can approve from the roster without hunting for the terminal pane. This is an
    /// explicit per-prompt human action — NOT auto-bypass; the operator chooses to approve each one.
    /// </summary>
    [RelayCommand]
    private void ApprovePermission(AgentPaneViewModel? pane)
    {
        if (pane?.CurrentPty is not { } pty) return;
        _ = pty.WriteAsync(pane.Runtime == AgentRuntimeKind.Codex ? "\r" : "1\r");
        if (pane.NoteTerminalInteraction()) RefreshAttention();
        Timeline.Add(DateTimeOffset.Now, pane.DisplayName, "approved prompt", pane.BorderColorHex);
    }

    /// <summary>Immediately terminates a live agent (graceful PTY dispose, no checkpoint).</summary>
    [RelayCommand]
    private Task KillAgent(AgentPaneViewModel? pane) => KillAgentCore(pane, force: false);

    /// <summary>Force-kills a stuck agent — SIGKILLs its OS process tree by PID.</summary>
    [RelayCommand]
    private Task ForceKillAgent(AgentPaneViewModel? pane) => KillAgentCore(pane, force: true);

    private async Task KillAgentCore(AgentPaneViewModel? pane, bool force)
    {
        if (pane is null) return;
        await pane.KillAsync(force);
        AttentionQueue.Remove(pane);          // a dead agent no longer needs you
        RefreshAttention();
        RefreshInstruments();
        Timeline.Add(DateTimeOffset.Now, pane.DisplayName, force ? "force-killed" : "killed", pane.BorderColorHex);
    }

    /// <summary>
    /// Removes a dead agent's pane from the roster + dock so its prefix is free to re-spawn. Refuses while the
    /// agent is still Live (kill it first) or if it has children (would orphan them / break the authority tree).
    /// </summary>
    [RelayCommand]
    private void RemoveAgent(AgentPaneViewModel? pane)
    {
        if (pane is null || pane.State == SessionState.Live) return;
        if (Panes.Any(p => p.ParentPrefix == pane.Prefix)) return;
        RemoveAgentPane(pane);
        Timeline.Add(DateTimeOffset.Now, pane.DisplayName, "removed", pane.BorderColorHex);
    }

    // ── Graceful shut down (top-bar): checkpoint every active agent, then close ───────────────────

    /// <summary>Wired by the shell to a modal confirm (message → true to proceed). Null → proceed without asking.</summary>
    public Func<string, Task<bool>>? ConfirmShutdownAsync { get; set; }

    /// <summary>Wired by the shell to the app's graceful shutdown (<c>desktop.Shutdown()</c> → the
    /// ShutdownRequested handler that disposes the VM/watchers). Never <c>Environment.Exit</c>.</summary>
    public Action? RequestShutdown { get; set; }

    /// <summary>Per-agent bound on the shutdown checkpoint so one stuck agent can't hang the whole close.</summary>
    private static readonly TimeSpan ShutdownCheckpointTimeout = TimeSpan.FromSeconds(20);

    /// <summary>An agent that should be checkpointed at shutdown: Live with a live PTY (skip already
    /// dehydrated / exited / unspawned).</summary>
    private static bool IsActiveForShutdown(AgentPaneViewModel p) => p.State == SessionState.Live && p.CurrentPty is not null;

    /// <summary>
    /// Checkpoint every ACTIVE agent (dehydrate = checkpoint context + graceful PTY dispose), then request
    /// the app's graceful close. A per-agent timeout means a stuck agent can't hang shutdown; agents that
    /// fail to checkpoint (or have no saved-context path → in-place checkpoint fallback) are FLAGGED, never
    /// silently dropped. The bus/channel + saved-context docs persist so a relaunch can rehydrate.
    /// </summary>
    [RelayCommand]
    private async Task Shutdown()
    {
        var active = Panes.Where(IsActiveForShutdown).ToList();

        if (ConfirmShutdownAsync is not null)
        {
            var msg = active.Count == 0
                ? "Shut down the cockpit?"
                : $"Checkpoint & shut down {active.Count} active agent{(active.Count == 1 ? "" : "s")}?";
            if (!await ConfirmShutdownAsync(msg)) return;   // operator cancelled — nothing touched
        }

        var flags = new List<string>();
        foreach (var pane in active)
        {
            Timeline.Add(DateTimeOffset.Now, pane.DisplayName, "checkpointing for shutdown…", pane.BorderColorHex);
            var note = await CheckpointForShutdownAsync(pane);
            if (note is not null) flags.Add(note);
        }
        if (flags.Count > 0)
            Timeline.Add(DateTimeOffset.Now, "workspace", "shutdown — " + string.Join("; ", flags), "#E5A05A");

        RequestShutdown?.Invoke();
    }

    /// <summary>Checkpoint one agent for shutdown; returns a flag note on any problem, or null on a clean
    /// dehydrate. Bounded by <see cref="ShutdownCheckpointTimeout"/> so a hang can't stall the close.</summary>
    private async Task<string?> CheckpointForShutdownAsync(AgentPaneViewModel pane)
    {
        try
        {
            // CanDehydrate (Live + has a saved-context path) → full dehydrate (checkpoint + graceful dispose).
            if (pane.DehydrateCommand.CanExecute(null))
            {
                await pane.DehydrateAsync(CancellationToken.None).WaitAsync(ShutdownCheckpointTimeout);
                return pane.State == SessionState.Dehydrated
                    ? null
                    : $"{pane.Prefix} closed without checkpoint — dehydrate did not complete";
            }

            // No saved-context path → best-effort in-place checkpoint, and flag it.
            await WriteInPlaceCheckpointAsync(pane).WaitAsync(ShutdownCheckpointTimeout);
            return $"{pane.Prefix} closed without a full checkpoint — no saved-context path";
        }
        catch (TimeoutException) { return $"{pane.Prefix} checkpoint timed out"; }
        catch (Exception ex) { return $"{pane.Prefix} checkpoint failed ({ex.GetType().Name})"; }
    }

    /// <summary>
    /// "Close empty docks": collapses any leftover NESTED empty document areas (split/tile regions with no
    /// documents) so the layout reflows into the freed space. The sole centre surface is preserved (you
    /// always want somewhere to open documents). Complements the automatic last-document-close collapse.
    /// </summary>
    [RelayCommand]
    private void TidyEmptyDocks()
    {
        if (_dockFactory is null || Layout is not global::Dock.Model.Core.IDock root) return;
        foreach (var dock in StyloagentDockFactory.EmptyCollapsibleDocks(root))
            _dockFactory.CollapseDock(dock);
    }

    [ObservableProperty]
    private DocLibraryViewModel? _docLibrary;

    [ObservableProperty]
    private ProposedTeamViewModel? _proposedTeam;

    [ObservableProperty]
    private IssuesViewModel? _issues;

    [ObservableProperty]
    private GitGraphViewModel? _gitGraph;

    [ObservableProperty]
    private ChangesViewModel? _changes;

    [ObservableProperty]
    private RouterViewModel? _router;

    /// <summary>The operator-question top-bar VM (ask_operator). Null until the fleet MCP server starts.</summary>
    [ObservableProperty]
    private OperatorQuestionsViewModel? _operatorQuestions;

    private IGitLog? _gitLog;
    private WorktreeGitWatcher? _gitWatcher;

    private ProjectConfig? _project;
    // The repo the project opened against — captured at InitializeAsync time (before AttachProject sets
    // _project) so the Git panel can fall back to it for agents without their own worktree.
    private string? _repoRoot;
    private RouterHost? _routerHost;

    // Per-agent markdown log writer (session-'s AgentLogWriter, item-3 slice 1). Driven off the same
    // hook Stop stream as the badges; wired in AttachProject because the project root — which locates
    // the sidecar logs dir the reader ("Log (this agent)") also uses — is only known once a project is
    // attached (the hooksDir is under system temp, so the writer can't self-locate). Held in a field so
    // it outlives the wiring and is visible to readers of this type.
    private AgentLogWriter? _agentLogWriter;

    // Operator-question hub (bus-'s ask_operator): pending questions the human answers from the top bar.
    // Constructed with the MCP server (StartFleetServerAsync); OperatorQuestions (below) mirrors its pending
    // set for the banner, and the shell reconciles per-pane markers off it (HookState stays hook-driven).
    private OperatorQuestionHub? _operatorQuestionHub;

    // open_document (bus-'s DocumentOpenHub): an agent asks the cockpit to surface a doc; we open it on the
    // doc surface via the shared OpenDocumentByPath, marshalled to the UI thread — mirrors the question hub.
    private Styloagent.Core.Attention.DocumentOpenHub? _documentOpenHub;

    private IFactory? _factory;
    private StyloagentDockFactory? _dockFactory;
    private BusViewModel? _busViewModel;

    // Compaction resilience: watches each live agent's context fill and fires CheckpointNeeded ONCE when it
    // climbs past 0.80 (re-arms after a compaction shrinks it), so we nudge the agent to write its resume doc
    // BEFORE a compaction hits. The PreCompact hook is the fallback net if that nudge was missed.
    private readonly Styloagent.Core.Sessions.ContextCheckpointMonitor _checkpointMonitor = new();

    // Priority message delivery: pushes new channel messages to their recipient agents per the
    // project's PriorityPolicy (ESC-break for interrupt, defer-until-idle for next-prompt, HUD-only
    // otherwise). Built in InitializeAsync; policy refreshed in AttachProject.
    private MessageDeliveryService? _deliveryService;
    private ChannelDeliveryCoordinator? _deliveryCoordinator;

    // Channel root (.styloagent/channel) — where the send_message MCP tool writes the .md trace.
    private string? _channelRoot;

    /// <summary>The activity timeline: a merged, newest-first feed of hook operations + bus messages.</summary>
    public TimelineViewModel Timeline { get; } = new();

    // Runtime state for AddAgent
    private IReadOnlyList<AgentManifestEntry> _seededEntries = Array.Empty<AgentManifestEntry>();
    private readonly HashSet<string> _openedPrefixes = new();
    private IPtyLauncher? _launcher;
    private IFileWatcher? _watcher;
    private IGitService? _git;
    private int _genericAgentCounter;
    private AgentRuntimeKind _defaultAgentRuntime = AgentRuntimeKind.Claude;

    // Extra args appended to the first pane's session when launched in overview mode.
    private IReadOnlyList<string> _overviewSystemPromptArgs = Array.Empty<string>();

    // Hook state channel (§4.4): each spawned claude reports lifecycle events into a shared
    // drop-dir; we route them to the owning pane by a per-pane hook id.
    private HookChannel? _hookChannel;
    private readonly Dictionary<string, AgentPaneViewModel> _panesByHookId = new();

    // ── Federated repo instances (Bug A): each opened repo runs fully independently — own channel, own
    // hooks/delivery, own agent set. Panes are tagged by their instance's repoRoot so an instance's
    // coordinator only nudges its own agents (never the primary's), the fork-B invariant. ───────────────
    private readonly List<RepoInstanceState> _repoInstances = new();
    private readonly Dictionary<AgentPaneViewModel, string> _paneRepoRoot = new();

    // ── Diagram cockpit ───────────────────────────────────────────────────────

    private readonly List<DiagramDocumentViewModel> _openDiagrams = new();
    private DispatcherTimer? _diagramDebounceTimer;

    // ── Attention auto-reveal (Task 4) ────────────────────────────────────────

    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(4);
    private InteractionMonitor _interaction = new();
    private DispatcherTimer? _idleTimer;

    /// <summary>
    /// Test seam: when set, <see cref="InitializeAsync"/> passes this clock to the
    /// <see cref="InteractionMonitor"/> so tests can control time. Reset in a finally block.
    /// Production callers leave this null (real UtcNow is used).
    /// </summary>
    internal static Func<DateTimeOffset>? InteractionClockForTest;

    /// <summary>Internal counter: number of times auto-reveal (no focus) was triggered (for tests).</summary>
    internal int AutoActivateCountForTest;

    /// <summary>Internal counter: number of times jump-to-next (with focus) was triggered (for tests).</summary>
    internal int JumpFocusCountForTest;

    // ── MCP server ────────────────────────────────────────────────────────────

    private StyloagentMcpServer? _mcpServer;

    /// <summary>True once the in-process MCP server is up and listening.</summary>
    public bool McpServerRunning => _mcpServer?.IsRunning ?? false;

    /// <summary>Non-null when the server failed to start (degraded mode — agents still launch).</summary>
    public string? McpServerWarning { get; private set; }

    /// <summary>
    /// Starts the in-process MCP server and fleet controller. Idempotent; degrades gracefully on
    /// failure so agents can still launch without MCP support.
    /// </summary>
    public async Task StartFleetServerAsync()
    {
        if (_mcpServer is not null) return;
        try
        {
            // Operator-question hub (bus-'s ask_operator): the chosen answer routes back to the asker as a
            // normal bus message via our existing SendBusMessage — the same path every message takes, so
            // MCP-native pull delivery applies. OperatorQuestions mirrors the pending set for the banner;
            // its PendingChanged reconciles per-pane markers (HookState stays hook-driven — see the design).
            _operatorQuestionHub ??= new OperatorQuestionHub(
                new OperatorQuestionStore(),
                (to, subject, body) => SendBusMessage(
                    new MessageRequest(OperatorQuestionHub.OperatorPrefix, to, subject, body, "normal")));
            if (OperatorQuestions is null)
            {
                OperatorQuestions = new OperatorQuestionsViewModel(_operatorQuestionHub);
                OperatorQuestions.PendingChanged += ReconcileOperatorQuestionPanes;
            }

            // open_document: an agent's request opens the (already scope-checked + existing) file on the doc
            // surface. Marshalled to the UI thread; the asker + reason land on the timeline so the operator
            // sees WHO surfaced it and WHY.
            _documentOpenHub ??= new Styloagent.Core.Attention.DocumentOpenHub();
            _documentOpenHub.Opened += (_, req) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleDocumentOpen(req));

            // Feed the live hooksDir so check_inbox drains the SAME PendingInbox store the delivery hooks fill.
            _mcpServer = await StyloagentMcpServer.StartAsync(new FleetController(this), new RouterController(this),
                _hookChannel?.HooksDirectory, _operatorQuestionHub, _documentOpenHub).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            McpServerWarning = $"MCP server unavailable — agents cannot spawn subteams: {ex.Message}";
            System.Diagnostics.Trace.WriteLine($"[Styloagent] {McpServerWarning}");
        }
    }

    /// <summary>
    /// Returns the <c>--mcp-config</c> args for a given agent prefix, or an empty list when the
    /// server is not running (degraded mode).
    /// </summary>
    public IReadOnlyList<string> McpArgsFor(string prefix)
        => _mcpServer is { IsRunning: true } s ? s.McpConfigArgs(prefix) : Array.Empty<string>();

    private IReadOnlyList<string> CodexMcpArgsFor(string prefix)
        => _mcpServer is { IsRunning: true } s ? s.CodexMcpConfigArgs(prefix) : Array.Empty<string>();

    /// <summary>Returns the router root directory for the active project, or null when no project is loaded.</summary>
    public string? RouterRootOrNull => _project?.RouterRoot;

    // ── Fleet management ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FleetHudText))]
    private bool _fleetPaused;

    /// <summary>Guardrail limits for this session (read from fleet.yaml on project attach).</summary>
    public FleetPolicy FleetPolicy { get; set; } = FleetPolicy.Default;

    /// <summary>Number of currently-open agent panes.</summary>
    public int FleetCount => Panes.Count;

    /// <summary>Passthrough for XAML binding (FleetPolicy is not observable).</summary>
    public int MaxFleet => FleetPolicy.MaxFleet;

    /// <summary>Passthrough for XAML binding (FleetPolicy is not observable).</summary>
    public int MaxDepth => FleetPolicy.MaxDepth;

    /// <summary>Formatted HUD line for the Agents header: "fleet &lt;count&gt;/&lt;maxFleet&gt; · depth &lt;maxDepth&gt; max".</summary>
    public string FleetHudText => $"fleet {FleetCount}/{FleetPolicy.MaxFleet} · depth {FleetPolicy.MaxDepth} max";

    /// <summary>Toggles the fleet-paused flag, blocking all governor-checked spawns when true.</summary>
    [RelayCommand]
    private void PauseFleet() => FleetPaused = !FleetPaused;

    // private ctor — callers must use InitializeAsync.
    private MainWindowViewModel()
    {
        Panes.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(FleetCount));
            OnPropertyChanged(nameof(FleetHudText));
            RefreshInstruments();
            ArmDiagramDebounce();
            RebuildRoster();   // keep the repo-grouped roster in sync as agents come and go (BUG 3)

            // Track each pane's lifecycle so dehydrate/rehydrate land on the activity timeline.
            if (e.NewItems is not null)
                foreach (AgentPaneViewModel p in e.NewItems)
                {
                    _paneState[p] = p.State;
                    p.PropertyChanged += OnPaneLifecycleChanged;
                    WireThrottle(p);   // watch its output for API-error / rate-limit episodes
                }
            if (e.OldItems is not null)
                foreach (AgentPaneViewModel p in e.OldItems)
                    UnwireThrottle(p);
        };
    }

    // ── API throttle / rate-limit badge (session- detects; cockpit- renders) ─────────────────────

    // One detector per pane, subscribed to its PTY output; kept with its feed handler so removal can
    // unsubscribe (no leaked closure pinning a dead pane's session).
    private readonly Dictionary<AgentPaneViewModel, (Styloagent.Core.Sessions.ApiThrottleDetector Detector, Action<string> Feed)> _throttle = new();

    // One fleet-wide retry scheduler (session-'s ThrottleRetryScheduler): a throttled agent that doesn't
    // self-clear gets escalating "retry" bus messages (backoff-bounded); resuming cancels its pending retries.
    private Styloagent.Core.Sessions.ThrottleRetryScheduler? _throttleScheduler;

    private void WireThrottle(AgentPaneViewModel pane)
    {
        if (_throttle.ContainsKey(pane)) return;
        var detector = new Styloagent.Core.Sessions.ApiThrottleDetector(pane.Prefix);
        detector.Changed += e =>
        {
            // Changed fires on the PTY output thread — marshal the flag update to the UI thread.
            void Apply()
            {
                ApplyThrottleEvent(pane, e);
                // Drive the retry scheduler: throttled → start escalating retries; resumed → cancel them.
                if (e.IsThrottled) _throttleScheduler?.OnThrottled(e.AgentId);
                else _throttleScheduler?.OnResumed(e.AgentId);
            }
            try { if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply); }
            catch { Apply(); }   // no UI thread (headless test) → apply inline
        };
        Action<string> feed = chunk => detector.Feed(chunk, DateTimeOffset.UtcNow);
        pane.Output += feed;
        _throttle[pane] = (detector, feed);
    }

    /// <summary>The scheduler's post-retry hook: a visible "retry — rate-limited" bus message to the
    /// throttled agent, riding the EXISTING delivery→injector path (so ce42d82 compose-defer protects the
    /// operator). No new Core/Channel seam — reuses the bus send + coordinator this VM already owns.
    /// <paramref name="attempt"/> is the scheduler's 1-based retry number (it passes attempt+1) — used as-is.</summary>
    internal async Task PostThrottleRetryAsync(string agentId, int attempt)
    {
        var sig = Panes.FirstOrDefault(p => p.Prefix == agentId)?.LastThrottleSignature ?? "rate-limited";
        await SendBusMessage(new MessageRequest(
            "watchdog-", agentId, $"retry {attempt}: rate-limited",
            $"You appear rate-limited ({sig}) — retry your last request when able.", "normal"));
        if (_deliveryCoordinator is not null) await _deliveryCoordinator.PumpAsync();
    }

    private void UnwireThrottle(AgentPaneViewModel pane)
    {
        if (_throttle.Remove(pane, out var t))
            pane.Output -= t.Feed;
    }

    /// <summary>Apply a throttle transition to its pane (throttled → set signature/since; resumed → clear).</summary>
    internal static void ApplyThrottleEvent(AgentPaneViewModel pane, Styloagent.Core.Sessions.ThrottleEvent e)
    {
        pane.IsThrottled = e.IsThrottled;
        pane.LastThrottleSignature = e.Signature;
        pane.ThrottledSince = e.IsThrottled ? e.Timestamp : null;
    }

    /// <summary>Rebuild <see cref="RosterGroups"/> from the current panes + known repos so each repo's
    /// fleet roots at its own overview with repo attribution (BUG 3). Runs on the UI thread (panes and
    /// the repo set only change there).</summary>
    private void RebuildRoster()
    {
        var groups = RosterGrouping.Build(Panes.ToList(), _repos, p => RepoNameForPrefix(p.Prefix));
        RosterGroups.Clear();
        foreach (var g in groups) RosterGroups.Add(g);

        // Colour each agent's DOCK TAB by its repo (same hue as the roster group) so mixed-repo tabs are
        // distinguishable; only in a multi-repo workspace (single-repo → no band, like the roster header).
        foreach (var g in groups)
            foreach (var pane in g.Agents)
            {
                pane.RepoBandColorHex = g.ShowHeader ? g.ColorHex : null;
                pane.RepoBandTooltip = g.ShowHeader ? $"repo: {g.RepoName}" : null;
            }
    }

    // ── Roster reparent (drag-drop v2a): edit the authority hierarchy WITHIN a repo ──────────────

    /// <summary>The outcome of a reparent attempt — <see cref="Applied"/>, operator-<see cref="Cancelled"/>,
    /// or rejected with a <see cref="Reason"/> (the view snaps the drag back and shows it).</summary>
    public sealed record ReparentResult(bool Applied, bool Cancelled, string? Reason)
    {
        public static ReparentResult Ok() => new(true, false, null);
        public static ReparentResult Cancel() => new(false, true, null);
        public static ReparentResult Reject(string reason) => new(false, false, reason);
    }

    /// <summary>Wired by the shell to a modal confirm (message → true to apply). Null → apply without asking.</summary>
    public Func<string, Task<bool>>? ConfirmReparentAsync { get; set; }

    /// <summary>
    /// Reparent <paramref name="dragged"/> under <paramref name="newOwner"/> (drag-drop v2a): change its
    /// <c>ParentPrefix</c> and recompute <c>Depth</c> for it + its descendants, then rebuild the grouped
    /// roster. WITHIN-REPO only — cross-repo cascades through delivery routing so it snaps back. Every drop
    /// is guarded BEFORE applying: the existing Core authority lint (cycle / root / missing-parent /
    /// owner-with-worktree), <see cref="MaxDepth"/>, and a confirm. Returns why it was rejected/cancelled.
    /// </summary>
    public async Task<ReparentResult> ReparentAgentAsync(AgentPaneViewModel? dragged, AgentPaneViewModel? newOwner)
    {
        if (dragged is null || newOwner is null) return ReparentResult.Reject("nothing to move");
        if (ReferenceEquals(dragged, newOwner)) return ReparentResult.Reject("can't move an agent onto itself");
        if (string.Equals(dragged.ParentPrefix, newOwner.Prefix, StringComparison.Ordinal))
            return ReparentResult.Reject($"{dragged.DisplayName} is already under {newOwner.DisplayName}");
        if (string.IsNullOrEmpty(dragged.ParentPrefix))
            return ReparentResult.Reject($"can't reparent {dragged.DisplayName} — it's a repo overview (the root)");

        // v2a: within-repo only. Cross-repo changes the agent's repo identity (delivery routing keyed by
        // (repo,prefix), spawn lineage, _paneRepoRoot) — deferred to v2b.
        if (!string.Equals(RepoNameForPrefix(dragged.Prefix), RepoNameForPrefix(newOwner.Prefix), StringComparison.Ordinal))
            return ReparentResult.Reject("cross-repo moves aren't supported yet");

        // Guard 1: run the EXISTING Core authority lint (read-only) over the PROPOSED tree.
        var proposed = Panes.Select(p => new Styloagent.Core.Architecture.AuthorityNode(
            p.Prefix,
            ReferenceEquals(p, dragged) ? newOwner.Prefix : p.ParentPrefix,
            p.WorktreePath is not null)).ToList();
        var violations = Styloagent.Core.Architecture.AuthorityTreeLint.Check(proposed);
        if (violations.Count > 0)
            return ReparentResult.Reject(ReparentReason(violations[0], dragged, newOwner));

        // Guard 2: the moved subtree must not exceed the max depth.
        int newDepth = newOwner.Depth + 1;
        if (newDepth + SubtreeHeight(dragged) > MaxDepth)
            return ReparentResult.Reject($"that move would exceed the max depth of {MaxDepth}");

        // Guard 3: confirm — it edits the authority hierarchy.
        if (ConfirmReparentAsync is not null)
        {
            int descendants = DescendantsOf(dragged).Count;
            var msg = descendants == 0
                ? $"Move {dragged.DisplayName} under {newOwner.DisplayName}?"
                : $"Move {dragged.DisplayName} (and its {descendants} descendant{(descendants == 1 ? "" : "s")}) under {newOwner.DisplayName}?";
            if (!await ConfirmReparentAsync(msg)) return ReparentResult.Cancel();
        }

        // Apply: re-owner + recompute depths for the moved subtree, then regroup the roster.
        dragged.ParentPrefix = newOwner.Prefix;
        RecomputeDepths(dragged, newDepth);
        RebuildRoster();
        Timeline.Add(DateTimeOffset.Now, dragged.DisplayName, $"reparented under {newOwner.DisplayName}", dragged.BorderColorHex);
        return ReparentResult.Ok();
    }

    private static string ReparentReason(Styloagent.Core.Architecture.AuthorityViolation v,
        AgentPaneViewModel dragged, AgentPaneViewModel newOwner) => v.Kind switch
    {
        "cycle"               => $"can't move {dragged.DisplayName} under its own descendant",
        "owner-has-worktree"  => $"{newOwner.DisplayName} holds a worktree — it can't own agents",
        "multiple-roots" or "no-root" => $"can't reparent {dragged.DisplayName} — it would break the single root",
        "missing-parent"      => $"{newOwner.DisplayName} isn't in the fleet",
        _                     => v.Detail,
    };

    /// <summary>Every descendant of <paramref name="node"/> (its subtree, excluding itself).</summary>
    private List<AgentPaneViewModel> DescendantsOf(AgentPaneViewModel node)
    {
        var result = new List<AgentPaneViewModel>();
        var stack = new Stack<AgentPaneViewModel>(Panes.Where(p => p.ParentPrefix == node.Prefix));
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            result.Add(n);
            foreach (var c in Panes.Where(p => p.ParentPrefix == n.Prefix)) stack.Push(c);
        }
        return result;
    }

    /// <summary>Longest descendant chain below <paramref name="node"/> (0 for a leaf).</summary>
    private int SubtreeHeight(AgentPaneViewModel node)
    {
        var children = Panes.Where(p => p.ParentPrefix == node.Prefix).ToList();
        return children.Count == 0 ? 0 : 1 + children.Max(SubtreeHeight);
    }

    /// <summary>Set <paramref name="node"/>'s depth and cascade depth+1 to its descendants.</summary>
    private void RecomputeDepths(AgentPaneViewModel node, int depth)
    {
        node.Depth = depth;
        foreach (var c in Panes.Where(p => p.ParentPrefix == node.Prefix).ToList())
            RecomputeDepths(c, depth + 1);
    }

    private readonly Dictionary<AgentPaneViewModel, SessionState> _paneState = new();

    private void OnPaneLifecycleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentPaneViewModel.State) || sender is not AgentPaneViewModel p) return;
        var prev = _paneState.TryGetValue(p, out var s) ? s : SessionState.Unspawned;
        _paneState[p] = p.State;

        string? desc = (p.State, prev) switch
        {
            (SessionState.Dehydrated, _)                  => "dehydrated",
            (SessionState.Live, SessionState.Dehydrated)  => "rehydrated",
            _                                             => null,   // Unspawned→Live is the initial spawn (hook covers it)
        };
        if (desc is not null)
        {
            Timeline.Add(DateTimeOffset.Now, p.DisplayName, desc, p.BorderColorHex);
            RefreshInstruments();
        }
    }

    /// <summary>
    /// Factory method: seeds the channel from <paramref name="channelRoot"/>, loads
    /// (or creates) the presentation sidecar, wires up the first agent pane, and
    /// returns a fully-initialised view-model.
    /// </summary>
    public static async Task<MainWindowViewModel> InitializeAsync(
        string channelRoot,
        IPtyLauncher launcher,
        IFileWatcher watcher,
        IGitReader? gitReader = null,
        string? repoRoot = null,
        string? presentationPath = null,
        string? overviewSystemPromptPath = null,
        IGitService? gitService = null,
        IGitLog? gitLog = null,
        string? overviewColorHex = null,
        IReadOnlyList<Styloagent.Core.Workspace.RepoOverview>? extraOverviews = null,
        AgentRuntimeKind defaultAgentRuntime = AgentRuntimeKind.Claude,
        CancellationToken ct = default)
    {
        var vm = new MainWindowViewModel();
        vm._defaultAgentRuntime = defaultAgentRuntime;
        vm._repoRoot = repoRoot;
        vm._launcher = launcher;
        vm._watcher = watcher;
        vm._git = gitService;
        vm._gitLog = gitLog;
        if (gitLog is not null)
            vm.GitGraph = new GitGraphViewModel(gitLog);
        if (gitService is IGitDiff gitDiff && gitService is IGitWrite gitWrite && gitService is Styloagent.Git.IGitBranch gitBranch && gitService is Styloagent.Git.IGitStash gitStash)
            vm.Changes = new ChangesViewModel(gitService, gitDiff, gitWrite, gitBranch, gitStash);

        // Hook state channel (§4.4): drop-dir under temp, one per app run. Failure to set it up
        // must never stop agents launching — hooks are observe-only, so degrade gracefully.
        try
        {
            var hooksDir = Path.Combine(Path.GetTempPath(), "styloagent-hooks", Guid.NewGuid().ToString("N"));
            vm._hookChannel = new HookChannel(hooksDir);
            vm._hookChannel.EventReceived += vm.OnHookEvent;
            vm._hookChannel.Start();
        }
        catch
        {
            vm._hookChannel = null; // agents still launch, just without state badges
        }

        // Agents ARE the git worktrees under a configured repo (point-at-a-repo, detect
        // worktrees): each launches claude in that worktree. Falls back to channel-seeded
        // agents when no git reader is supplied (e.g. in unit tests).
        IReadOnlyList<AgentManifestEntry> entries;
        if (overviewSystemPromptPath is not null)
        {
            // Overview mode: seed a single overview agent; worktree/channel seeding is skipped.
            // Start the fleet server NOW — before the overview AgentSession is built — so that
            // McpArgsFor("overview-") is non-empty when the session args are assembled.
            await vm.StartFleetServerAsync().ConfigureAwait(false);

            string overviewRoot = repoRoot ?? Directory.GetCurrentDirectory();
            var overviewEntry = new AgentManifestEntry(
                Prefix: "overview-",
                Repo: overviewRoot,
                Worktree: overviewRoot,
                LaunchPromptPath: string.Empty,
                RestartPromptPath: string.Empty,
                SavedContextPath: string.Empty,
                Transport: AgentTransport.Local,
                Runtime: defaultAgentRuntime);

            // Resume the overview from its OWN context doc when the channel carries one — revive YOU, not a
            // blank overseer. Write a restart prompt (identity + re-read your context doc + resume + stay in
            // scope) and wire it as the overview's launch/restart prompt + saved-context, so spawn injects it
            // and the compaction guard points at it. Best-effort; a fresh overview otherwise.
            var overviewCtx = Path.Combine(channelRoot, "saved-context", "overview-context.md");
            if (File.Exists(overviewCtx))
            {
                try
                {
                    var restart = Path.Combine(channelRoot, "launch-prompts", "overview-restart.md");
                    if (!File.Exists(restart))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(restart)!);
                        File.WriteAllText(restart, Styloagent.Core.Hooks.HydrationText.For(
                            "overview-", overviewCtx, Path.Combine(channelRoot, "PROTOCOL.md"), channelRoot));
                    }
                    overviewEntry = overviewEntry with
                    {
                        LaunchPromptPath = restart,
                        RestartPromptPath = restart,
                        SavedContextPath = overviewCtx,
                    };
                }
                catch { /* best-effort revive; fall back to a fresh overview */ }
            }

            // The overview opens + spawns; the channel's parked fleet (every agent with a saved-context
            // doc) is added to the roster as un-opened seeded entries, so the operator can revive each
            // from its restart prompt via +Add agent — it cold-starts from its own saved-context doc.
            // Only entries[0] (the overview) is spawned here, so this never auto-launches the fleet.
            // An optional worktrees.yaml maps each prefix to the repo it should revive in (else the default).
            var worktreeMap = Styloagent.Core.Channel.WorktreeMapReader.Read(channelRoot);
            var channelFleet = await new ChannelManifestSeeder().SeedAsync(channelRoot, worktreeMap);
            var persistedRuntimes = await LoadPersistedRuntimesAsync(channelRoot);
            channelFleet = channelFleet
                .Select(e => persistedRuntimes.TryGetValue(e.Prefix, out var runtime) ? e with { Runtime = runtime } : e)
                .ToList();
            entries = new[] { overviewEntry }
                .Concat(channelFleet
                    .Where(e => e.Prefix != "overview-")
                    // Channel-seeded entries come from the persisted manifest. Keep each agent's runtime
                    // so reopening a mixed parked fleet does not turn Codex agents into Claude (or vice versa).
                    .Select(e => EnsureRevivePrompt(e, channelRoot)))
                .ToList();

            vm._overviewSystemPromptArgs = File.Exists(overviewSystemPromptPath)
                ? new[] { "--append-system-prompt", File.ReadAllText(overviewSystemPromptPath) }
                : Array.Empty<string>();
        }
        else if (gitReader is not null)
        {
            var root = repoRoot
                ?? Environment.GetEnvironmentVariable("STYLOAGENT_REPO")
                ?? Directory.GetCurrentDirectory();
            var worktrees = await gitReader.ListWorktreesAsync(root, ct);
            entries = worktrees.Count > 0
                ? worktrees.Select(w => WorktreeEntry(w, root) with { Runtime = defaultAgentRuntime }).ToList()
                : new[] { WorktreeEntry(new GitWorktree(root, Path.GetFileName(root.TrimEnd('/', '\\')), string.Empty), root) with { Runtime = defaultAgentRuntime } };
        }
        else
        {
            entries = (await new ChannelManifestSeeder().SeedAsync(channelRoot, new Dictionary<string, string>()))
                .Select(e => e with { Runtime = defaultAgentRuntime })
                .ToList();
        }
        vm._seededEntries = entries;

        // The bus feed is routed/coloured by the CHANNEL's own agent prefixes, which are
        // independent of the worktree agents shown as terminals.
        var channelPrefixes = (await new ChannelManifestSeeder()
                .SeedAsync(channelRoot, new Dictionary<string, string>()))
            .Select(e => e.Prefix).ToList();
        // Single PendingInbox instance, shared by delivery (writes the pending ledger) and the bus
        // viewer's PickupProjection (reads it for the WORKING pill). Null when delivery isn't MCP-wired,
        // in which case pickup reads false everywhere → the viewer just shows WAITING/DONE.
        var pending = vm._hookChannel is null
            ? null
            : new Styloagent.Core.Channel.PendingInbox(vm._hookChannel.HooksDirectory);
        var pickup = new Styloagent.Core.Attention.PickupProjection(pending);
        // The pickup ledger + pending notes live under the temp hooks `deliver/` dir (not the channel),
        // so the bus viewer polls it to bring the WORKING pill live when a recipient drains a note.
        string? pickupWatchDir = vm._hookChannel is null
            ? null
            : Styloagent.Core.Channel.DeliveryHookCommands.DeliverDir(vm._hookChannel.HooksDirectory);

        // Durable operator read-state (seen/archived) lives beside the channel under .styloagent/, so the
        // operator's Archive + seen survive a cockpit restart. Operator-LOCAL — never in the shared channel.
        var viewStateDir = Directory.GetParent(channelRoot)?.FullName ?? channelRoot;
        var busViewState = new JsonBusViewState(Path.Combine(viewStateDir, "bus-view-state.json"));

        vm._busViewModel = new BusViewModel(
            channelRoot, channelPrefixes, viewState: busViewState,
            isPickedUp: pickup.IsPickedUp, pickupWatchDir: pickupWatchDir)
        {
            OpenDocument = vm.OpenBusMessageDocument,   // double-click a message → its full markdown
            ThreadOpener = vm.OpenBusThreadDocument,     // popout a thread → carousel through it
        };

        // Priority delivery: seed the "already seen" set with the current backlog (so startup does
        // not deliver old messages), then push newly-arrived messages on each bus reload. The policy
        // starts at Default and is refreshed in AttachProject once a project is known.
        vm._channelRoot = channelRoot;
        vm._deliveryService = new MessageDeliveryService(
            PriorityPolicy.Default, new PtyMessageInjector(vm.ResolvePty), pending);
        vm._deliveryCoordinator = new ChannelDeliveryCoordinator(
            channelRoot, channelPrefixes, vm._deliveryService, vm.SnapshotLiveAgents);
        await vm._deliveryCoordinator.SeedAsync();
        vm._busViewModel.Reloaded += () => _ = vm._deliveryCoordinator.PumpAsync();

        // One fleet retry scheduler (session-'s ThrottleRetryScheduler): a throttled agent that doesn't
        // self-clear gets escalating "retry" bus messages riding the delivery→injector path PostThrottleRetryAsync
        // owns. delay:null → prod Task.Delay backoff (20s/60s/180s); the detector transitions drive it.
        vm._throttleScheduler = new Styloagent.Core.Sessions.ThrottleRetryScheduler(vm.PostThrottleRetryAsync);

        // Compaction resilience: when an agent's context climbs past 0.80, nudge it to write its resume doc.
        vm._checkpointMonitor.CheckpointNeeded += vm.SendCheckpointNudge;

        // Debounced .git watcher: refreshes the Git panel when the selected worktree changes on disk
        // (e.g. an agent commits). Subscribed here; Watch() is called in RefreshGitPanelFor so it
        // always tracks the currently-selected pane.
        vm._gitWatcher = new WorktreeGitWatcher();
        vm._gitWatcher.Changed += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.RefreshGitPanelFor(vm.SelectedPane));
        // Structured git-op visibility: a branch switch in the watched worktree logs a timeline line
        // (the panel refresh above already reflects the new state).
        vm._gitWatcher.BranchChanged += (_, branch) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => vm.LogGitBranchChange(branch));

        if (entries.Count == 0)
        {
            var emptyFactory = new StyloagentDockFactory(null, vm._busViewModel);
            vm._dockFactory = emptyFactory;
            var emptyLayout = emptyFactory.CreateLayout();
            vm.Layout = emptyLayout;
            emptyFactory.InitLayout(emptyLayout);
            vm._factory = emptyFactory;
            return vm;
        }

        var first = WithWorkingDir(entries[0]);
        vm._openedPrefixes.Add(first.Prefix);

        // Load or derive the presentation.
        var store = new PresentationStore();
        AgentPresentation? presentation = null;

        if (presentationPath != null && File.Exists(presentationPath))
        {
            var all = await store.LoadAsync(presentationPath);
            presentation = all.FirstOrDefault(p => p.Prefix == first.Prefix);
        }

        presentation ??= new AgentPresentation(
            Prefix: first.Prefix,
            DisplayName: first.Prefix.TrimEnd('-'),
            BorderColorHex: PresentationStore.DefaultColorFor(first.Prefix));

        // Multi-repo: colour the primary overview by its repo hue too, so repo 0 is glanceable like the
        // rest (single-repo passes null → the default/saved colour, released path unchanged).
        if (overviewColorHex is not null)
            presentation = presentation with { BorderColorHex = overviewColorHex };

        string firstHookId = vm.ReserveHookId(first.Prefix);
        var session = new AgentSession(first, launcher, watcher,
            vm.LaunchArgsFor(firstHookId, first, vm._overviewSystemPromptArgs));

        vm.Pane = new AgentPaneViewModel(
            session,
            first,
            presentation.DisplayName,
            presentation.BorderColorHex)
        {
            InteractionRecorder = vm._interaction.RecordInput,
            Host = vm,
        };
        vm.Panes.Add(vm.Pane);
        vm.SelectedPane = vm.Pane;
        vm._panesByHookId[firstHookId] = vm.Pane;

        // Apply the injectable clock for tests; production path uses default UtcNow.
        if (InteractionClockForTest is not null)
            vm._interaction = new InteractionMonitor(InteractionClockForTest);

        // Idle-gated auto-reveal timer: on each tick, reveal the head waiter when the
        // human has been quiet for the IdleWindow.  This drives trigger (b) — a waiter
        // that arrived while the human was busy is surfaced as soon as they go idle —
        // complementing the hook-arrival path that already covers trigger (a).
        vm._idleTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => vm.OnIdleTick());
        vm._idleTimer.Start();

        // Assign the input recorder on the first pane after _interaction may have been replaced above.
        vm.Pane.InteractionRecorder = vm._interaction.RecordInput;

        var dockFactory = new StyloagentDockFactory(vm.Pane, vm._busViewModel);
        vm._dockFactory = dockFactory;
        vm._factory = dockFactory;
        dockFactory.ActiveDockableChanged += vm.OnActiveDockableChanged;
        var layout = dockFactory.CreateLayout();
        vm.Layout = layout;
        if (layout is not null)
            dockFactory.InitLayout(layout);

        // A pane owns an agent terminal: launch the selected runtime immediately.
        // The view attaches to CurrentPty when it renders.
        _ = vm.Pane.SpawnAsync();

        // Multi-repo: each additional repo brings its own overview onto the SHARED bus (the primary repo
        // above anchors on `overview-`). Added here — same thread, same dock, before the window is realised —
        // so every repo overview is built identically to the primary.
        if (extraOverviews is { Count: > 0 })
            foreach (var ov in extraOverviews)
                vm.AddRepoOverview(ov);

        var docRepoRoot = repoRoot ?? Environment.GetEnvironmentVariable("STYLOAGENT_REPO") ?? Directory.GetCurrentDirectory();
        vm.DocLibrary = new DocLibraryViewModel(docRepoRoot, channelRoot, vm.OpenMarkdownDocument,
            nameSearch: term => vm._searchIndex.SearchByName(term, 200))
        {
            ShowSystemMapCommand = vm.ShowSystemMapCommand,
            ShowBusSequenceCommand = vm.ShowBusSequenceCommand,
            ShowArchitectureCommand = vm.ShowArchitectureCommand,
        };
        // Build the (content) search index OFF the startup critical path — it reads every doc's full text,
        // which was a big chunk of the "show everything is slow" cost. With the doc library now lazy, the
        // index is no longer needed to populate the tree, so it can finish in the background.
        _ = Task.Run(() => vm.BuildSearchIndex(docRepoRoot, channelRoot));
        vm.Timeline.OpenSource = vm.OpenSourceDocument;
        vm.Timeline.OpenDiff = vm.OpenDiffDocument;

        return vm;
    }

    /// <summary>
    /// Adds a new agent pane to the center DocumentDock at runtime.
    /// Picks the next seeded manifest entry not already opened; if all seeded entries
    /// are open (or none were seeded), synthesizes a generic entry.
    /// </summary>
    // TODO: future — detect installed agents and show a dropdown instead of auto-picking.
    [RelayCommand]
    public void AddAgent() => AddAgent(runtimeOverride: null);

    [RelayCommand]
    public void AddClaude() => AddAgent(AgentRuntimeKind.Claude);

    [RelayCommand]
    public void AddCodex() => AddAgent(AgentRuntimeKind.Codex);

    private void AddAgent(AgentRuntimeKind? runtimeOverride)
    {
        if (_dockFactory is null || _launcher is null || _watcher is null)
            return;

        var documentDock = _dockFactory.DocumentDock;
        var rootDock = _dockFactory.RootDock;
        if (documentDock is null || rootDock is null)
            return;

        // Pick the next unseeded entry, or synthesize a generic one.
        var nextEntry = _seededEntries.FirstOrDefault(e => !_openedPrefixes.Contains(e.Prefix));

        AgentManifestEntry entry;
        AgentPresentation presentation;

        if (nextEntry is not null)
        {
            _openedPrefixes.Add(nextEntry.Prefix);
            entry = runtimeOverride is { } runtime ? nextEntry with { Runtime = runtime } : nextEntry;
            presentation = new AgentPresentation(
                Prefix: entry.Prefix,
                DisplayName: entry.Prefix.TrimEnd('-'),
                BorderColorHex: PresentationStore.DefaultColorFor(entry.Prefix));
        }
        else
        {
            _genericAgentCounter++;
            var prefix = $"agent-{_genericAgentCounter}-";
            entry = new AgentManifestEntry(
                Prefix: prefix,
                Repo: string.Empty,
                Worktree: string.Empty,
                LaunchPromptPath: string.Empty,
                RestartPromptPath: string.Empty,
                SavedContextPath: SavedContextPathFor(prefix),   // so it can be dehydrated / parked
                Transport: AgentTransport.Local,
                Runtime: runtimeOverride ?? _defaultAgentRuntime);
            presentation = new AgentPresentation(
                Prefix: prefix,
                DisplayName: prefix.TrimEnd('-'),
                BorderColorHex: PresentationStore.DefaultColorFor(prefix));
        }

        entry = WithWorkingDir(entry);
        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher, LaunchArgsFor(hookId, entry));
        var owner = OverviewPane();   // the overview owns agents added to its fleet
        var paneVm = new AgentPaneViewModel(
            session,
            entry,
            presentation.DisplayName,
            presentation.BorderColorHex)
        {
            InteractionRecorder = _interaction.RecordInput,
            Host = this,
            ParentPrefix = owner is not null && owner.Prefix != entry.Prefix ? owner.Prefix : null,
            Depth = owner is not null && owner.Prefix != entry.Prefix ? owner.Depth + 1 : 0,
        };
        Panes.Add(paneVm);
        SelectedPane = paneVm;
        _panesByHookId[hookId] = paneVm;

        // The pane IS a Dock Document — add it directly (Id/Title/CanFloat set in its ctor).
        _dockFactory.AddDockable(documentDock, paneVm);
        _dockFactory.SetActiveDockable(paneVm);
        _dockFactory.SetFocusedDockable(rootDock, paneVm);

        // In a tiled mode, re-tile so the new pane gets its own tile rather than a hidden tab.
        if (LayoutMode != CockpitLayoutMode.Tabs) RebuildCenterLayout();

        // Launch the selected agent runtime in the new pane immediately.
        _ = paneVm.SpawnAsync();
        PersistRevivalMetadata(entry);
    }

    /// <summary>
    /// Opens an additional repo's overview agent as a pane on the shared bus (multi-repo workspaces).
    /// Mirrors <see cref="AddAgent"/> but launches the selected runtime in that repo's root with the repo's
    /// own system prompt and the fleet MCP, coloured by the repo's hue. The primary repo's overview is opened by
    /// <see cref="InitializeAsync"/>; this adds every additional repo. Idempotent per prefix.
    /// </summary>
    public void AddRepoOverview(Styloagent.Core.Workspace.RepoOverview overview)
        => AddRepoOverview(overview, _hookChannel, _channelRoot, _repoRoot, _project?.ProtocolPath, overview.RepoRoot);

    /// <summary>
    /// As <see cref="AddRepoOverview(Styloagent.Core.Workspace.RepoOverview)"/> but routes the launched
    /// overview agent's hooks to a SPECIFIC <paramref name="hooks"/> channel + channel/repo roots (a
    /// federated repo instance's OWN hooks + delivery), and tags its pane with
    /// <paramref name="instanceRepoRoot"/> so that instance's coordinator only nudges its own agents. The
    /// primary overload passes the primary channel, unchanged.
    /// </summary>
    public void AddRepoOverview(
        Styloagent.Core.Workspace.RepoOverview overview,
        HookChannel? hooks, string? channelRoot, string? repoRoot, string? protocolPath, string instanceRepoRoot)
    {
        if (_dockFactory is null || _launcher is null || _watcher is null)
            return;
        var documentDock = _dockFactory.DocumentDock;
        var rootDock = _dockFactory.RootDock;
        if (documentDock is null || rootDock is null)
            return;
        if (!_openedPrefixes.Add(overview.Prefix))
            return;   // already open

        var entry = WithWorkingDir(new AgentManifestEntry(
            Prefix: overview.Prefix,
            Repo: overview.RepoRoot,
            Worktree: overview.RepoRoot,
            LaunchPromptPath: string.Empty,
            RestartPromptPath: string.Empty,
            SavedContextPath: string.Empty,
            Transport: AgentTransport.Local,
            Runtime: _defaultAgentRuntime));

        // The specialist team travels with the repo: append THIS repo's own system prompt.
        var systemPromptArgs = File.Exists(overview.SystemPromptPath)
            ? new[] { "--append-system-prompt", File.ReadAllText(overview.SystemPromptPath) }
            : Array.Empty<string>();

        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher,
            LaunchArgsFor(hookId, entry, hooks, channelRoot, repoRoot, protocolPath, systemPromptArgs));

        var paneVm = new AgentPaneViewModel(session, entry, overview.Prefix.TrimEnd('-'), overview.ColorHex)
        {
            InteractionRecorder = _interaction.RecordInput,
            Host = this,
        };
        Panes.Add(paneVm);
        _panesByHookId[hookId] = paneVm;
        _paneRepoRoot[paneVm] = instanceRepoRoot;   // tag for the per-instance liveAgents filter
        _dockFactory.AddDockable(documentDock, paneVm);

        // Launch the selected runtime in the repo's root immediately (the primary pane stays selected).
        _ = paneVm.SpawnAsync();
    }

    // ── Live open-repo / second-instance gesture (Bug A) ─────────────────────────────────────────

    /// <summary>Folder picker for the open-repo gesture; wired by the window (a StorageFolderPicker over it).</summary>
    public IFolderPicker? RepoFolderPicker { get; set; }

    private RepoInstanceCoordinator? _repoCoordinator;

    /// <summary>
    /// Open a chosen repo — one that has its OWN <c>.styloagent/</c> — as a fully independent federated
    /// instance mid session. Flow (pick folder → resolve canonical git root via <c>ResolveRepoRootAsync</c> →
    /// confirm it's a Styloagent instance → hand off to the federation opener, de-duping) lives in
    /// <see cref="RepoInstanceCoordinator"/>; <see cref="OpenFederatedInstanceAsync"/> does the launch.
    /// </summary>
    [RelayCommand]
    private async Task OpenRepoInstance()
    {
        if (RepoFolderPicker is null || _git is null)
            return;

        _repoCoordinator ??= new RepoInstanceCoordinator(
            RepoFolderPicker,
            (path, ct) => _git.ResolveRepoRootAsync(path, ct),
            new CockpitRepoInstanceOpener(new Styloagent.Core.Channel.RepoChannelResolver(), OpenFederatedInstanceAsync));

        LogRepoInstanceResult(await _repoCoordinator.OpenAsync());
    }

    /// <summary>
    /// Launch a repo as a fully independent federated instance (Bug A): its OWN hooks channel, its OWN
    /// delivery coordinator over its own channel (liveAgents filtered to it), its own bus pane, and its
    /// overview agent — none of it crossing into the primary fleet. Cross-repo <c>send_message(repo:)</c>
    /// between instances is the follow-on co-land with bus-; this makes the instance itself live.
    /// </summary>
    private async Task OpenFederatedInstanceAsync(Styloagent.Core.Channel.RepoChannel channel)
    {
        if (_dockFactory?.DocumentDock is not { } dock)
            return;

        // 1) Own HookChannel (own hooks dir) so this instance's agents get real delivery + PickedUp + badges.
        HookChannel? hooks = null;
        try
        {
            var hooksDir = Path.Combine(Path.GetTempPath(), "styloagent-hooks", Guid.NewGuid().ToString("N"));
            hooks = new HookChannel(hooksDir);
            hooks.EventReceived += OnHookEvent;
            hooks.Start();
        }
        catch { hooks = null; }

        // 2) Own delivery stack via bus-'s blessed factory: PendingInbox under this instance's hooks dir,
        //    liveAgents filtered to THIS repo so its coordinator never nudges the primary fleet, and a
        //    repo-SCOPED injector so a nudge can't type into a same-named primary agent (the (repoRoot,
        //    prefix) gotcha — the filter picks the right agent, this delivers to the right PTY).
        var hooksDirectory = hooks?.HooksDirectory
            ?? Path.Combine(Path.GetTempPath(), "styloagent-hooks", Guid.NewGuid().ToString("N"));
        var inst = await new Styloagent.Core.Channel.RepoInstanceFactory().CreateAsync(
            channel.RepoRoot, hooksDirectory, PriorityPolicy.Default,
            new PtyMessageInjector(id => ResolvePtyForRepo(channel.RepoRoot, id)),
            () => SnapshotLiveAgentsForRepo(channel.RepoRoot));
        await inst.Coordinator.SeedAsync();

        // 3) Its own bus feed as a document tab, keyed to ITS OWN pickup store (its WORKING pill reads the
        //    instance's PendingInbox, not the primary's); each reload pumps ITS coordinator.
        var pickup = new Styloagent.Core.Attention.PickupProjection(inst.Pending);
        var bus = new BusViewModel(inst.Channel.ChannelRoot, inst.Channel.Prefixes, isPickedUp: pickup.IsPickedUp)
        {
            OpenDocument = OpenBusMessageDocument,
            ThreadOpener = OpenBusThreadDocument,
        };
        bus.Reloaded += () => _ = inst.Coordinator.PumpAsync();
        _dockFactory.AddDockable(dock, new RepoBusDocumentViewModel(channel.RepoRoot, channel.Name, bus));

        // 4) Launch its overview agent, hooks routed to THIS instance + pane tagged with its repoRoot.
        var prefix = RepoInstancePrefix(channel.Name);
        var overview = new Styloagent.Core.Workspace.RepoOverview(
            Prefix: prefix,
            RepoRoot: channel.RepoRoot,
            SystemPromptPath: Path.Combine(channel.RepoRoot, ".styloagent", "system-prompt.md"),
            RepoIndex: _repoInstances.Count + 1,
            ColorHex: PresentationStore.DefaultColorFor(prefix),
            IsPrimary: false);
        // Register the repo BEFORE adding its overview pane so RepoNameForPrefix + the repo-grouped roster
        // attribute this instance's agents to IT (not the primary) the moment they appear (BUG 3).
        AddWorkspaceRepo(overview);
        AddRepoOverview(overview, hooks, inst.Channel.ChannelRoot, channel.RepoRoot,
            Path.Combine(inst.Channel.ChannelRoot, "PROTOCOL.md"), channel.RepoRoot);

        _repoInstances.Add(new RepoInstanceState(inst, hooks, bus));
        Timeline.Add(DateTimeOffset.Now, "workspace", $"launched {channel.Name} instance ({prefix})", "#8899BB");
    }

    /// <summary>Repo display name → a clean, unique-ish channel prefix (lower-case alphanumerics, trailing '-').
    /// Mirrors <c>WorkspaceConfig.PrefixFor</c> (internal to Core), so an instance's overview keys consistently.</summary>
    private static string RepoInstancePrefix(string name)
    {
        var cleaned = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        return (cleaned.Length == 0 ? "repo" : cleaned) + "-";
    }

    /// <summary>One live federated repo instance: its delivery stack (<see cref="Styloagent.Core.Channel.RepoInstanceChannel"/>
    /// — has .Channel/.Coordinator/.Pending/.Delivery), its own hooks channel, and its bus feed.</summary>
    private sealed record RepoInstanceState(
        Styloagent.Core.Channel.RepoInstanceChannel Instance, HookChannel? Hooks, BusViewModel Bus);

    /// <summary>Surface the gesture's outcome on the activity timeline (rejects + errors; silent on cancel).</summary>
    private void LogRepoInstanceResult(OpenRepoInstanceResult result)
    {
        string? note = result.Status switch
        {
            OpenRepoInstanceStatus.Opened        => null,   // OpenFederatedInstanceAsync logs "launched …" itself
            OpenRepoInstanceStatus.Cancelled     => null,   // operator dismissed the picker — no noise
            OpenRepoInstanceStatus.AlreadyOpen   => $"{RepoName(result.RepoRoot)} is already open",
            OpenRepoInstanceStatus.NotARepo      => result.Message ?? "not a git repository",
            OpenRepoInstanceStatus.NotStyloagent => result.Message ?? "not a Styloagent instance",
            OpenRepoInstanceStatus.Failed        => $"couldn't open {RepoName(result.RepoRoot)}: {result.Message}",
            _ => null,
        };
        if (note is not null)
            Timeline.Add(DateTimeOffset.Now, "workspace", note, "#8899BB");
    }

    private static string RepoName(string? root)
        => string.IsNullOrEmpty(root) ? "repo" : Path.GetFileName(root!.TrimEnd('/', '\\'));

    /// <summary>Wires the ProposedTeam VM against a project's proposed-agents.yaml. Idempotent.</summary>
    public void AttachProject(ProjectConfig project)
    {
        _project = project;
        RaiseTitleChanged();   // project root known → title can show its name even before repos are set

        // Drive the per-agent log writer off the same hook Stop stream that feeds the badges. Wired here
        // (not at HookChannel creation) because project.Root — which locates the sidecar logs dir the
        // "Log (this agent)" reader opens (AgentLogPathFor) — is only known now. Same root on both sides so
        // writer and reader agree on the file. Guarded so a re-attach never double-subscribes; the writer
        // is best-effort and can't throw into the hook path.
        if (_hookChannel is not null && _agentLogWriter is null)
        {
            _agentLogWriter = new AgentLogWriter(AgentLogWriter.LogsDirFor(project.Root));
            _hookChannel.EventReceived += _agentLogWriter.OnHookEvent;
        }

        FleetPolicy = FleetPolicyReader.Read(project.FleetPolicyPath);
        if (_deliveryService is not null)
            _deliveryService.Policy = PriorityPolicyReader.Read(project.PriorityPolicyPath);
        OnPropertyChanged(nameof(MaxFleet));
        OnPropertyChanged(nameof(MaxDepth));
        OnPropertyChanged(nameof(FleetHudText));
        ProposedTeam?.Dispose();
        ProposedTeam = new ProposedTeamViewModel(project.ProposedAgentsPath, project.TeamPath, SpawnProposedAsync);
        Issues = new IssuesViewModel(project.IssuesDir, OpenDocumentByPath);

        // Start (or restart) the RouterHost whenever a project is attached so the coordinator
        // drives the ledger at project.RouterRoot.  Dispose the previous host first (idempotent).
        Router = new RouterViewModel(project.RouterRoot);
        Router.Refresh(); // initial load
        _routerHost?.Dispose();
        _routerHost = new RouterHost(
            project.RouterRoot,
            d => Dispatcher.UIThread.Post(() => OnRouterDecision(d)));
    }

    /// <summary>
    /// Called on the UI thread for each <see cref="Styloagent.Core.Router.RouterDecision"/> applied
    /// by <see cref="RouterHost"/>.  Refreshes the Router panel so grants/expiries surface live.
    /// Never throws.
    /// </summary>
    private void OnRouterDecision(Styloagent.Core.Router.RouterDecision d)
    {
        try
        {
            Router?.Refresh();
            var root = _project?.RouterRoot ?? string.Empty;
            System.Diagnostics.Trace.WriteLine($"[Router:{root}] {d.Action} {d.Prefix} on {d.Env}/{d.Name}");
        }
        catch { /* must never propagate */ }
    }

    /// <summary>
    /// Files an issue an agent reported over MCP into the project's <c>.styloagent/issues/</c> and
    /// refreshes the Issues panel. Runs on the UI thread (marshalled by the controller).
    /// </summary>
    public IssueOutcome ReportIssue(IssueRequest req)
    {
        if (_project is null) return IssueOutcome.Fail("no active project");
        if (string.IsNullOrWhiteSpace(req.Title)) return IssueOutcome.Fail("title is required");

        try
        {
            var issue = Styloagent.Core.Issues.IssueStore.Write(
                _project.IssuesDir, req.Reporter, req.Title, req.Detail ?? string.Empty,
                req.Severity ?? "medium", DateTimeOffset.Now);
            Issues?.Refresh();
            return IssueOutcome.Ok(issue.Id);
        }
        catch (Exception ex)
        {
            return IssueOutcome.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Runs the gated wrap-up for the agent identified by <paramref name="callerPrefix"/>. Requires an
    /// active project and that the agent was spawned with a worktree. Invoked on the UI thread but awaits
    /// the (slow) test/merge/cleanup off it, so the cockpit stays responsive; the continuation resumes on
    /// the UI thread to update the UI-bound state.
    /// </summary>
    public async Task<WrapUpOutcome> WrapUpAsync(string callerPrefix)
    {
        if (_project is null) return new WrapUpOutcome(WrapUpStatus.KeptUncommitted, "no active project", null);
        if (_git is null) return new WrapUpOutcome(WrapUpStatus.KeptUncommitted, "git unavailable", null);

        var pane = Panes.FirstOrDefault(p => p.Prefix == callerPrefix);
        if (pane?.WorktreePath is null || pane.WorktreeBranch is null)
            return new WrapUpOutcome(WrapUpStatus.KeptUncommitted,
                $"{callerPrefix} has no worktree to wrap up.", null);

        var policy = GitPolicyReader.Read(_project.GitPolicyPath);
        var svc = new WrapUpService(_git, new ProcessTestRunner());
        var req = new WrapUpRequest(callerPrefix, _project.Root, pane.WorktreePath, pane.WorktreeBranch);

        // await (not .GetAwaiter().GetResult()): wrap-up runs the test suite + merge + cleanup — seconds to
        // minutes. FleetController marshals this onto the UI thread, so blocking here froze the whole cockpit.
        // Awaiting releases the UI thread during the git/test I/O; the continuation resumes on it to update
        // the UI-bound collections below (Issues, pane, git panel), so those stay UI-thread-safe.
        var outcome = await svc.WrapUpAsync(req, policy, _project.IssuesDir);

        Issues?.Refresh();
        if (outcome.Merged)
        {
            pane.WorktreePath = null;
            pane.WorktreeBranch = null;
        }
        if (pane == SelectedPane) RefreshGitPanelFor(pane);
        else if (_git is not null) _ = pane.RefreshGitStatusAsync(_git);
        return outcome;
    }

    /// <summary>
    /// Sends a bus message on behalf of a caller agent (the <c>send_message</c> MCP tool): first wakes
    /// a parked (dehydrated) direct recipient so the message actually lands, then writes the durable
    /// <c>.md</c> trace and pumps delivery in-process. Writing the file only AFTER the rehydrate means
    /// no pump can "deliver" it to a dead PTY and mark it seen. Runs on the UI thread.
    /// </summary>
    public async Task<MessageOutcome> SendBusMessage(MessageRequest req)
    {
        if (_channelRoot is null) return MessageOutcome.Fail("no active channel");
        if (string.IsNullOrWhiteSpace(req.To)) return MessageOutcome.Fail("recipient (to) is required");
        if (string.IsNullOrWhiteSpace(req.Subject)) return MessageOutcome.Fail("subject is required");

        try
        {
            // Resolve the (possibly cross-repo) target channel. req.Repo blank/own-repo → the primary channel
            // (single-repo byte-identical); a federated instance's name/root → that instance's channel; an
            // unknown repo → fail loudly rather than silently drop. The sender's repo rides as From-Repo so a
            // reply routes home (stamped only cross-repo, so single-repo output is unchanged).
            var senderRoot = _repoRoot ?? _channelRoot;
            var sender = new Styloagent.Core.Channel.RepoChannel(
                senderRoot, Path.GetFileName(senderRoot.TrimEnd('/', '\\')), _channelRoot, Array.Empty<string>());
            var openRepos = new[] { sender }
                .Concat(_repoInstances.Select(i => i.Instance.Channel))
                .ToList();
            var target = Styloagent.Core.Channel.RepoMessageRouting.Resolve(sender, req.Repo, openRepos);
            if (target is null) return MessageOutcome.Fail($"unknown repo '{req.Repo}'");

            bool crossRepo = !string.Equals(target.ChannelRoot, _channelRoot, StringComparison.OrdinalIgnoreCase);

            // Auto-rehydrate a parked direct recipient (intra-repo only — cross-repo delivery is the target
            // instance's coordinator's job, and rehydrating here could wake a same-named PRIMARY pane).
            if (!crossRepo)
            {
                var recipient = ChannelMessageWriter.NormalizeRecipient(req.To);
                if (recipient != "all-")
                {
                    var pane = Panes.FirstOrDefault(p => p.Prefix == recipient);
                    if (pane is { State: SessionState.Dehydrated })
                        await pane.RehydrateAsync();
                }
            }

            var path = ChannelMessageWriter.Write(
                target.ChannelRoot, req.From, req.To, req.Subject, req.Body ?? string.Empty,
                req.Priority ?? "normal", DateTimeOffset.Now, fromRepo: crossRepo ? target.FromRepo : null);

            // Deliver now (not on the debounced watcher): pump the TARGET repo's coordinator cross-repo, the
            // primary's intra-repo.
            if (crossRepo)
            {
                var inst = _repoInstances.FirstOrDefault(i => string.Equals(
                    i.Instance.Channel.ChannelRoot, target.ChannelRoot, StringComparison.OrdinalIgnoreCase));
                _ = inst?.Instance.Coordinator.PumpAsync();
            }
            else
            {
                _ = _deliveryCoordinator?.PumpAsync();
            }

            var senderColor = Panes.FirstOrDefault(p => p.Prefix == req.From)?.BorderColorHex ?? "#8888AA";
            var repoTag = crossRepo ? $" @{req.Repo}" : "";
            Timeline.Add(DateTimeOffset.Now, req.From, $"→ {req.To} · {req.Subject}{repoTag}", senderColor);

            return MessageOutcome.Ok(path);
        }
        catch (Exception ex)
        {
            return MessageOutcome.Fail(ex.Message);
        }
    }

    /// <summary>Writes an immutable completion report for a thread, which makes the bus projection mark it DONE.</summary>
    public Task<MessageOutcome> ReplyToBusThreadAsync(string callerPrefix, string thread, string body)
    {
        if (_channelRoot is null) return Task.FromResult(MessageOutcome.Fail("no active channel"));
        if (string.IsNullOrWhiteSpace(thread)) return Task.FromResult(MessageOutcome.Fail("thread is required"));
        try
        {
            var path = ChannelMessageWriter.Reply(_channelRoot, callerPrefix, thread, body ?? string.Empty, DateTimeOffset.Now);
            var color = Panes.FirstOrDefault(p => p.Prefix == callerPrefix)?.BorderColorHex ?? "#8888AA";
            Timeline.Add(DateTimeOffset.Now, callerPrefix, $"completed · {thread}", color);
            return Task.FromResult(MessageOutcome.Ok(path));
        }
        catch (Exception ex)
        {
            return Task.FromResult(MessageOutcome.Fail(ex.Message));
        }
    }

    private int _consoleCount;

    /// <summary>
    /// Opens a plain shell terminal as a document tab — not an agent (no claude, no lifecycle). Useful
    /// as a scratch console in the cockpit. Added to the centre dock and focused.
    /// </summary>
    [RelayCommand]
    private void NewConsole()
    {
        if (_dockFactory?.DocumentDock is null || _launcher is null) return;
        var cwd = _repoRoot ?? Environment.GetEnvironmentVariable("STYLOAGENT_REPO") ?? Directory.GetCurrentDirectory();

        var n = ++_consoleCount;
        var console = new ConsolePaneViewModel($"console-{n}", $"Console {n}");
        _dockFactory.AddDockable(_dockFactory.DocumentDock, console);
        _dockFactory.SetActiveDockable(console);
        if (_dockFactory.RootDock is { } root) _dockFactory.SetFocusedDockable(root, console);

        _ = console.StartAsync(_launcher, cwd);
    }

    // ── Document search (Lucene, top-bar autosuggest) ────────────────────────
    private readonly Styloagent.Core.Docs.DocumentSearchIndex _searchIndex = new();

    /// <summary>Live document-search suggestions for the top-bar box (updated as the query changes).</summary>
    public ObservableCollection<Styloagent.Core.Docs.DocSearchHit> SearchResults { get; } = new();

    /// <summary>The top-bar search text — each change re-queries the Lucene index for suggestions.</summary>
    [ObservableProperty]
    private string _searchQuery = "";

    partial void OnSearchQueryChanged(string value)
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var hit in _searchIndex.Search(value, 8))
            SearchResults.Add(hit);
    }

    /// <summary>The chosen suggestion — selecting one opens the document and resets the box.</summary>
    [ObservableProperty]
    private Styloagent.Core.Docs.DocSearchHit? _selectedSearchResult;

    partial void OnSelectedSearchResultChanged(Styloagent.Core.Docs.DocSearchHit? value)
    {
        if (value is null) return;
        OpenDocumentByPath(value.FullPath);   // shared viewer-by-type dispatch (same as drag-to-surface)
        // Reset the box for the next search (deferred so we don't fight the AutoCompleteBox's own update).
        Dispatcher.UIThread.Post(() =>
        {
            SelectedSearchResult = null;
            SearchQuery = "";
            SearchResults.Clear();
        });
    }

    /// <summary>(Re)builds the document search index. Names+titles first (no file reads) so the doc
    /// library's in-pane by-name box answers almost immediately, then the full-text content streams in.</summary>
    private void BuildSearchIndex(string? repoRoot, string? channelRoot)
    {
        try
        {
            var entries = Styloagent.Core.Docs.DocLibraryReader.Read(repoRoot, channelRoot);
            _searchIndex.BuildNames(entries);   // filename+title field live first — powers SearchByName
            _searchIndex.Build(entries.Select(e => (e, SafeReadFile(e.FullPath))));   // full-text streams in
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainWindowViewModel] search index build failed: {ex}");
        }
    }

    private static string SafeReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// The overview (root) pane that owns the fleet: the <c>overview-</c> agent, or failing that the first
    /// rootless pane. Null when no overview is present (e.g. a bare worktree roster). Used to parent
    /// human-spawned agents to the overview so ownership shows in the lineage and the authority tree stays
    /// single-rooted.
    /// </summary>
    private AgentPaneViewModel? OverviewPane()
        => Panes.FirstOrDefault(p => p.Prefix == "overview-")
           ?? Panes.FirstOrDefault(p => string.IsNullOrEmpty(p.ParentPrefix));

    /// <summary>
    /// The saved-context checkpoint path for a spawned agent, under the channel's <c>saved-context/</c> dir
    /// (matching the reconstitution format: <c>&lt;prefix&gt;context.md</c>). Giving spawned agents this path
    /// lets them be dehydrated (parked to free their PTY) and later revived — without it, Dehydrate is
    /// disabled. Empty when there is no active channel.
    /// </summary>
    private string SavedContextPathFor(string prefix)
    {
        if (_channelRoot is null) return string.Empty;
        var dir = Path.Combine(_channelRoot, "saved-context");
        try { Directory.CreateDirectory(dir); } catch { /* checkpoint dir is best-effort */ }
        return Path.Combine(dir, $"{HookSettings.SanitizeAgentId(prefix)}context.md");
    }

    /// <summary>
    /// Turns a proposed subsystem into a live roster agent. Normal case: routes through the governed
    /// <see cref="SpawnChildAsync"/>, so a human-spawn gets the same governor + worktree + lineage as an
    /// agent's <c>spawn_agent</c>. Sole exception: with no overview owner (a bare worktree roster) this
    /// is a ROOT spawn — the parent-centric governor can't check a root without risking a second root
    /// (breaking single-rooted authority), so it creates the pane directly, still honouring the
    /// proposal's worktree decision.
    /// </summary>
    public async Task<SpawnOutcome> SpawnProposedAsync(ProposedAgent p)
    {
        var owner = OverviewPane();
        if (owner is not null && owner.Prefix != p.Prefix)
            return await SpawnChildAsync(new SpawnRequest(
                owner.Prefix, p.Prefix, p.Responsibility, p.Dir, p.LaunchPrompt, p.Worktree,
                Runtime: RuntimeName(_defaultAgentRuntime)));

        // Root / no-owner exception: establish the single root directly.
        string? worktreePath = null, worktreeBranch = null;
        if (p.Worktree)
        {
            var wt = await TryAddWorktreeAsync(p.Prefix);
            if (!wt.Ok) return SpawnOutcome.Reject(RejectReason.InvalidPrefix, $"worktree add failed: {wt.Error}");
            (worktreePath, worktreeBranch) = (wt.Path, wt.Branch);
        }
        var pane = CreatePaneForProposed(p, worktreeOverride: worktreePath, worktreeBranch: worktreeBranch,
            runtime: _defaultAgentRuntime);
        if (worktreePath is not null && _git is not null && pane is not null)
            _ = pane.RefreshGitStatusAsync(_git);
        return pane is null
            ? SpawnOutcome.Reject(RejectReason.InvalidPrefix, "could not create pane")
            : SpawnOutcome.Ok(p.Prefix);
    }

    /// <summary>Outcome of a worktree add: Ok (with Path/Branch, or all-null when the agent shares the repo), or a failure Error.</summary>
    private readonly record struct WorktreeAdd(bool Ok, string? Path, string? Branch, string? Error)
    {
        public static WorktreeAdd Shared => new(true, null, null, null);
        public static WorktreeAdd Failed(string? error) => new(false, null, null, error);
        public static WorktreeAdd Created(string path, string branch) => new(true, path, branch, null);
    }

    /// <summary>
    /// Creates an isolated <c>agent/&lt;prefix&gt;</c> worktree for a spawning agent when a git service and
    /// project are present. Returns <see cref="WorktreeAdd.Created"/> on success, <see cref="WorktreeAdd.Failed"/>
    /// if the add fails, or <see cref="WorktreeAdd.Shared"/> (Ok, null path/branch) when there is no git service
    /// or project — the agent then shares the repo. Awaits the add (a checkout) so the UI thread isn't blocked.
    /// </summary>
    private async Task<WorktreeAdd> TryAddWorktreeAsync(string prefix)
    {
        if (_git is null || _project is null) return WorktreeAdd.Shared;   // nothing to isolate; share the repo
        var existing = Panes.Where(p => p.WorktreePath is not null).Select(p => p.WorktreePath!);
        var (wtPath, wtBranch) = WorktreeNaming.For(_project.Root, prefix, existing);
        // await (not .GetAwaiter().GetResult()): git worktree add does a checkout. Spawn runs on the UI
        // thread (FleetController marshals it there; the roster Spawn button is on it too), so blocking here
        // froze the cockpit. Awaiting releases the UI thread during the add; the continuation resumes on it.
        var add = await _git.AddWorktreeAsync(_project.Root, wtPath, wtBranch);
        if (!add.Ok) return WorktreeAdd.Failed(add.Error);
        EnsureWorktreesIgnored(_project.Root);
        await EnsureLucidViewProvisionedAsync(_project.Root);
        return WorktreeAdd.Created(wtPath, wtBranch);
    }

    /// <summary>
    /// Fix 2: places a spawn's mission doc where the new agent can read it from its own checkout, and returns
    /// the launch prompt to inject — prefixed with a pointer to the doc. A worktree agent is cut from HEAD, so
    /// the doc goes INTO the worktree (committed on its branch); a shared agent gets it in the main tree. An
    /// empty <paramref name="missionDoc"/> leaves the launch prompt untouched. Best-effort: a placement failure
    /// is traced and the plain launch prompt is used, never failing the spawn.
    /// </summary>
    private async Task<string> PlaceMissionDocAsync(string prefix, string missionDoc, string launchPrompt, string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(missionDoc)) return launchPrompt;
        string? treeRoot = worktreePath ?? _project?.Root;
        if (string.IsNullOrWhiteSpace(treeRoot)) return launchPrompt;

        var result = await Styloagent.Git.WorktreeMissionDoc.PlaceAsync(
            treeRoot, prefix, missionDoc, commit: worktreePath is not null);
        if (!result.Ok)
        {
            System.Diagnostics.Trace.WriteLine($"[Styloagent] mission doc for {prefix} not placed: {result.Detail}");
            return launchPrompt;
        }
        // Tell the agent where its mission lives, then hand it its normal launch prompt.
        return $"Your mission doc is in your working tree at `{result.RelativePath}` — read it first.\n\n{launchPrompt}";
    }

    /// <summary>
    /// Governor-checked spawn from a parent agent. Builds fleet state, runs the governor,
    /// and on approval creates the pane with parent/depth lineage stamped in.
    /// </summary>
    public async Task<SpawnOutcome> SpawnChildAsync(SpawnRequest req)
    {
        var state = new FleetState(BuildFleetSnapshot().Members, FleetPolicy.MaxFleet, FleetPolicy.MaxDepth, FleetPaused);
        var decision = FleetGovernor.Check(state, req.ParentPrefix, req.Prefix);
        if (!decision.Allowed) return SpawnOutcome.Reject(decision.Reason!.Value, decision.Message);

        var runtime = RuntimeFromRequest(req.Runtime);
        var runtimeName = RuntimeName(runtime);
        var capabilities = BuildAgentCapabilities();
        if (!capabilities.Supports(runtimeName, req.Model, req.Effort))
            return SpawnOutcome.Reject(RejectReason.InvalidPrefix,
                $"unsupported agent selection: {runtimeName}/{req.Model ?? "default"}/{req.Effort ?? "default"}; call agent_capabilities");

        // Re-spawn recovery: the governor allows re-spawning over a crashed ("exited") ghost. Drop the
        // dead pane so the fresh spawn reclaims its slot instead of duplicating the prefix. Refuse if the
        // ghost still has children — removing it would orphan them and break the single-rooted authority tree.
        var ghost = Panes.FirstOrDefault(p => p.Prefix == req.Prefix);
        if (ghost is not null)
        {
            if (Panes.Any(p => p.ParentPrefix == req.Prefix))
                return SpawnOutcome.Reject(RejectReason.DuplicatePrefix,
                    $"'{req.Prefix}' has children — remove them before re-spawning it");
            RemoveAgentPane(ghost);
        }

        int parentDepth = Panes.First(p => p.Prefix == req.ParentPrefix).Depth;

        string? worktreePath = null, worktreeBranch = null;
        if (req.Worktree)
        {
            var wt = await TryAddWorktreeAsync(req.Prefix);
            if (!wt.Ok) return SpawnOutcome.Reject(RejectReason.InvalidPrefix, $"worktree add failed: {wt.Error}");
            (worktreePath, worktreeBranch) = (wt.Path, wt.Branch);
        }

        // Fix 2: hand a (worktree-isolated) agent its mission as a committed doc it can read from its own
        // checkout, then point its launch prompt at it. No mission doc → the launch prompt is used as-is.
        string launchPrompt = await PlaceMissionDocAsync(req.Prefix, req.MissionDoc, req.LaunchPrompt, worktreePath);
        var proposed = new ProposedAgent(req.Prefix, req.Responsibility, req.Dir, launchPrompt);
        var paneVm = CreatePaneForProposed(proposed, parentPrefix: req.ParentPrefix, depth: parentDepth + 1,
            worktreeOverride: worktreePath, worktreeBranch: worktreeBranch, runtime: runtime,
            model: req.Model, effort: req.Effort);
        if (worktreePath is not null && _git is not null)
            _ = paneVm!.RefreshGitStatusAsync(_git);
        return paneVm is null
            ? SpawnOutcome.Reject(RejectReason.InvalidPrefix, "could not create pane")
            : SpawnOutcome.Ok(req.Prefix);
    }

    /// <summary>
    /// Drops a crashed agent's pane so its prefix can be reclaimed by a re-spawn: removes its dockable,
    /// roster entry and hook mapping. The PTY is already dead (this runs only for an exited ghost), so
    /// there is nothing to kill — it is the inverse of the additions made in <see cref="CreatePaneForProposed"/>.
    /// </summary>
    private void RemoveAgentPane(AgentPaneViewModel pane)
    {
        if (_dockFactory is not null && pane.Owner is global::Dock.Model.Core.IDock)
            _dockFactory.RemoveDockable(pane, collapse: true);
        Panes.Remove(pane);
        foreach (var hookId in _panesByHookId.Where(kv => kv.Value == pane).Select(kv => kv.Key).ToList())
            _panesByHookId.Remove(hookId);
        if (ReferenceEquals(SelectedPane, pane)) SelectedPane = Panes.FirstOrDefault();
        RefreshInstruments();
    }

    /// <summary>Builds a fleet snapshot from the current roster (for list_fleet and SpawnChild).</summary>
    public FleetSnapshot BuildFleetSnapshot()
    {
        // A parked agent reports "dehydrated" (not its stale hook text) so the governor can tell a
        // rehydratable ghost apart from a crashed ("exited") one — the two are recovered differently.
        var members = Panes.Select(p => new FleetMember(
            p.Prefix, p.Responsibility, p.ParentPrefix, p.Depth,
            p.State == SessionState.Dehydrated ? "dehydrated" : (p.HookStateText ?? "running"))).ToList();
        return new FleetSnapshot(members, FleetPolicy.MaxFleet, FleetPolicy.MaxDepth, FleetPaused);
    }

    /// <summary>Reloads the repo capability catalog so MCP and new agents see edits immediately.</summary>
    public AgentCapabilities BuildAgentCapabilities()
        => AgentCapabilities.Load(_project?.Root ?? _repoRoot);

    // ── Fleet-control surface (the fleet_status / dehydrate / rehydrate / read_timeline MCP tools) ──

    /// <summary>Rich, live per-agent status — an orchestrator's situational-awareness snapshot.</summary>
    public FleetStatusReport BuildFleetStatus()
    {
        var agents = Panes.Select(p => new AgentStatus(
            Prefix: p.Prefix,
            Responsibility: p.Responsibility,
            State: HookStateName(p.HookState),
            Activity: p.StatusHeadline,
            IdleSeconds: p.LastActivityAt is { } t ? (int)Math.Max(0, (DateTimeOffset.UtcNow - t).TotalSeconds) : -1,
            Usage: p.UsageText,
            Worktree: p.WorktreePath is not null,
            Repo: RepoNameForPrefix(p.Prefix))).ToList();
        return new FleetStatusReport(agents, WorkingCount, WaitingCount, FleetPaused);
    }

    /// <summary>
    /// Which repo an agent belongs to (multi-repo workspaces): its own overview prefix if it is one,
    /// else it inherits its parent's repo up the spawn chain; falls back to the primary repo. Empty
    /// when no workspace repos are known (single-repo tests).
    /// </summary>
    private string RepoNameForPrefix(string prefix)
    {
        if (_repos.Count == 0) return "";
        var seen = new HashSet<string>();
        string? p = prefix;
        while (p is not null && seen.Add(p))
        {
            var hit = _repos.FirstOrDefault(r => r.Prefix == p);
            if (hit is not null) return hit.Name;
            p = Panes.FirstOrDefault(x => x.Prefix == p)?.ParentPrefix;
        }
        return _repos[0].Name;   // default: the primary (anchor) repo
    }

    /// <summary>Context-window fill at which an agent is nudged to dehydrate / hand off before its scope dilutes.</summary>
    private const double DilutionThreshold = 0.75;

    /// <summary>
    /// Scope-dilution guard: when a live agent's context window fills past <see cref="DilutionThreshold"/>,
    /// surface a one-time timeline nudge to dehydrate or hand work off (spawn a specialist) before the
    /// agent dilutes its scope. Resets with hysteresis so it can fire again after a compaction shrinks it.
    /// </summary>
    public void CheckContextDilution()
    {
        foreach (var pane in Panes)
        {
            if (pane.State != SessionState.Live) { pane.DilutionNudged = false; continue; }

            if (pane.ContextFraction >= DilutionThreshold && !pane.DilutionNudged)
            {
                pane.DilutionNudged = true;
                Timeline.Add(DateTimeOffset.Now, pane.DisplayName,
                    $"context {pane.ContextFraction * 100:0}% — consider dehydrating or handing off (spawn a specialist) before scope dilutes",
                    pane.BorderColorHex);
                RefreshInstruments();
            }
            else if (pane.ContextFraction < DilutionThreshold - 0.10)
            {
                pane.DilutionNudged = false;   // hysteresis: re-arm once it drops well below the line
            }
        }
    }

    /// <summary>The 0.80 checkpoint nudge (distinct from the 0.75 human dilution note — two signals, two
    /// audiences): deliver a bus message asking the agent to write its resume doc before a compaction hits.</summary>
    private void SendCheckpointNudge(string prefix)
    {
        var pane = Panes.FirstOrDefault(p => p.Prefix == prefix);
        _ = SendBusMessage(new MessageRequest(
            "cockpit-", prefix, "checkpoint your context",
            Styloagent.Core.Sessions.CheckpointNudge.For(prefix, SavedContextPathFor(prefix)), "normal"));
        Timeline.Add(DateTimeOffset.Now, pane?.DisplayName ?? prefix.TrimEnd('-'),
            "context ~80% — nudged to checkpoint its resume doc", pane?.BorderColorHex ?? "#8888AA");
    }

    /// <summary>PreCompact fallback (the safety net if the 0.80 nudge was missed): persist a best-effort
    /// resume anchor from the agent's transcript tail — ONLY if it hasn't authored its own doc
    /// (degrade-never-destroy, per <see cref="Styloagent.Core.Channel.InPlaceCheckpoint"/>). File-only —
    /// never frees the PTY.</summary>
    private async Task WriteInPlaceCheckpointAsync(AgentPaneViewModel pane)
    {
        string tail = string.Empty;
        if (pane.TranscriptPath is { } tp)
        {
            try { tail = await Task.Run(() => Styloagent.Core.Transcripts.TranscriptReader.ReadLastAssistantText(tp)) ?? string.Empty; }
            catch { /* best-effort: a missing/locked transcript just yields an empty anchor body */ }
        }
        var result = Styloagent.Core.Channel.InPlaceCheckpoint.Write(
            pane.Prefix, SavedContextPathFor(pane.Prefix), tail, DateTimeOffset.Now);
        void Log() => Timeline.Add(DateTimeOffset.Now, pane.DisplayName, $"checkpoint: {result.Detail}", pane.BorderColorHex);
        if (Dispatcher.UIThread.CheckAccess()) Log(); else Dispatcher.UIThread.Post(Log);
    }

    /// <summary>Test seam: run the PreCompact fallback checkpoint for an agent, awaitable.</summary>
    internal Task WriteInPlaceCheckpointForTest(string prefix)
    {
        var pane = Panes.FirstOrDefault(p => p.Prefix == prefix);
        return pane is null ? Task.CompletedTask : WriteInPlaceCheckpointAsync(pane);
    }

    /// <summary>The most recent <paramref name="limit"/> timeline operations (newest first).</summary>
    public IReadOnlyList<TimelineOp> ReadTimeline(int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 30 : limit, 1, 200);
        return Timeline.Entries.Take(limit)
            .Select(e => new TimelineOp(e.TimeText, e.Agent, e.Description)).ToList();
    }

    /// <summary>Suspends an agent (checkpoints its context, frees its PTY) by prefix.</summary>
    public async Task<string> DehydrateAgentByPrefixAsync(string prefix)
    {
        var pane = Panes.FirstOrDefault(p => p.Prefix == prefix);
        if (pane is null) return $"rejected: no agent '{prefix}'";
        await pane.DehydrateAsync();
        return pane.State == SessionState.Dehydrated
            ? $"dehydrated {prefix}"
            : $"rejected: {prefix} stayed live (no checkpoint target, or the checkpoint didn't ack)";
    }

    /// <summary>Resumes a dehydrated agent by prefix.</summary>
    public async Task<string> RehydrateAgentByPrefixAsync(string prefix)
    {
        var pane = Panes.FirstOrDefault(p => p.Prefix == prefix);
        if (pane is null) return $"rejected: no agent '{prefix}'";
        await pane.RehydrateAsync();
        return pane.State == SessionState.Live
            ? $"rehydrated {prefix}"
            : $"rejected: {prefix} did not resume (was it dehydrated?)";
    }

    private static string HookStateName(Styloagent.Core.Hooks.AgentHookState s) => s switch
    {
        Styloagent.Core.Hooks.AgentHookState.Working         => "working",
        Styloagent.Core.Hooks.AgentHookState.Idle            => "idle",
        Styloagent.Core.Hooks.AgentHookState.WaitingForHuman => "needs-you",
        Styloagent.Core.Hooks.AgentHookState.Exited          => "exited",
        _                                                    => "unknown",
    };

    /// <summary>Reads what an agent last said (its most recent assistant turn) from its transcript.</summary>
    public async Task<string> ReadAgentOutput(string prefix)
    {
        var pane = Panes.FirstOrDefault(p => p.Prefix == prefix);
        if (pane is null) return $"rejected: no agent '{prefix}'";
        var path = pane.TranscriptPath;
        if (path is null) return $"rejected: {prefix} has no transcript yet";

        var text = await Task.Run(() => Styloagent.Core.Transcripts.TranscriptReader.ReadLastAssistantText(path));
        return string.IsNullOrEmpty(text) ? $"({prefix} has produced no assistant output yet)" : text;
    }

    // ── File-touch registry: which agent last touched each file (coordination context) ──────────
    private sealed record FileTouch(string Agent, DateTimeOffset At, string Op);
    private readonly Dictionary<string, FileTouch> _fileTouches = new(StringComparer.OrdinalIgnoreCase);

    private void RecordFileTouch(string path, string agent, string op)
        => _fileTouches[path] = new FileTouch(agent, DateTimeOffset.Now, op);

    /// <summary>Who last touched <paramref name="path"/>, when, and how — so you can coordinate before editing.</summary>
    public string WhoTouched(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "rejected: path required";
        // Accept either an exact path or a file name; match the most recent by name if not exact.
        if (!_fileTouches.TryGetValue(path, out var t))
        {
            var name = Path.GetFileName(path);
            t = _fileTouches
                .Where(kv => string.Equals(Path.GetFileName(kv.Key), name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value.At)
                .Select(kv => kv.Value)
                .FirstOrDefault();
            if (t is null) return $"no record of any agent touching {path}";
        }
        var ago = (int)Math.Max(0, (DateTimeOffset.Now - t.At).TotalSeconds);
        return $"{t.Agent} last touched it {ago}s ago ({t.Op})";
    }

    /// <summary>The most recently touched files across the fleet: "path — agent (op, Ns ago)".</summary>
    public IReadOnlyList<string> RecentFiles(int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 200);
        return _fileTouches
            .OrderByDescending(kv => kv.Value.At)
            .Take(limit)
            .Select(kv =>
            {
                var ago = (int)Math.Max(0, (DateTimeOffset.Now - kv.Value.At).TotalSeconds);
                return $"{kv.Key} — {kv.Value.Agent} ({kv.Value.Op}, {ago}s ago)";
            })
            .ToList();
    }

    /// <summary>Searches the document library (Lucene) — top matches so an agent reads only the relevant docs.</summary>
    public IReadOnlyList<Styloagent.Core.Docs.DocSearchHit> SearchDocs(string query, int limit)
    {
        limit = Math.Clamp(limit <= 0 ? 8 : limit, 1, 30);
        return _searchIndex.Search(query ?? "", limit);
    }

    // ── Workspace repos (multi-repo) ─────────────────────────────────────────
    private IReadOnlyList<RepoInfo> _repos = Array.Empty<RepoInfo>();

    /// <summary>The current project's display name — the primary (anchor) repo's name, else the first known
    /// repo, else the project/repo root's folder name. Shown in the title so the operator (and multiple
    /// open cockpits) can tell which project this is. Empty only before any project/repo is known.</summary>
    public string ProjectName
    {
        get
        {
            var name = _repos.FirstOrDefault(r => r.Primary)?.Name ?? (_repos.Count > 0 ? _repos[0].Name : null);
            if (string.IsNullOrWhiteSpace(name))
            {
                var root = _project?.Root ?? _repoRoot;
                name = string.IsNullOrWhiteSpace(root) ? "" : RepoName(root);
            }
            return name ?? "";
        }
    }

    /// <summary>The OS window title: the project name first (so cockpit windows are distinguishable in the
    /// title bar / dock / window switcher), then the product name. Falls back to the product name alone.</summary>
    public string WindowTitle
        => string.IsNullOrWhiteSpace(ProjectName) ? "Styloagent Cockpit" : $"{ProjectName} — Styloagent Cockpit";

    /// <summary>Git sidebar title; includes the active repository only when a workspace has multiple repos.</summary>
    public string GitSidebarTitle
        => _repos.Count > 1 && SelectedPane is not null ? $"Git · {RepoNameForPrefix(SelectedPane.Prefix)}" : "Git";

    private void RaiseTitleChanged()
    {
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(GitSidebarTitle));
    }

    /// <summary>
    /// Records the open workspace's repos (from its overview list) so the <c>list_repos</c> MCP tool and
    /// repo-grouped UI can enumerate them. A single repo becomes a one-entry list. Set once at startup.
    /// </summary>
    public void SetReposFromOverviews(IReadOnlyList<Styloagent.Core.Workspace.RepoOverview> overviews)
    {
        _repos = overviews.Select(RepoInfoFor).ToList();
        var workspace = new Styloagent.Core.Workspace.WorkspaceConfig(
            "", "", "", _channelRoot ?? "", "",
            overviews.Select(o => new Styloagent.Core.Workspace.RepoRef(
                o.RepoRoot, Path.GetFileName(o.RepoRoot.TrimEnd('/', '\\')), o.RepoIndex)).ToList(),
            overviews.Count <= 1);
        var roots = Styloagent.Core.Docs.DocumentLibraryRoots.For(workspace);
        DocLibrary?.SetRepositoryRoots(roots.Repositories, roots.ChannelRoot, roots.LogsRoot);
        RebuildRoster();   // repo set changed → re-attribute + regroup the roster (BUG 3)
        RaiseTitleChanged();   // primary repo now known → refresh the title
    }

    /// <summary>
    /// Register a repo opened LIVE (the federated open-repo gesture) so <see cref="RepoNameForPrefix"/> and
    /// the repo-grouped roster recognise its agents — otherwise the live-opened repo's fleet mis-attributes
    /// to the primary repo and its children nest under the wrong overview (BUG 3). Idempotent by prefix.
    /// </summary>
    public void AddWorkspaceRepo(Styloagent.Core.Workspace.RepoOverview overview)
    {
        if (_repos.Any(r => r.Prefix == overview.Prefix)) return;
        _repos = _repos.Append(RepoInfoFor(overview)).ToList();
        RebuildRoster();
        RaiseTitleChanged();
    }

    private static RepoInfo RepoInfoFor(Styloagent.Core.Workspace.RepoOverview o) => new(
        Name: Path.GetFileName(o.RepoRoot.TrimEnd('/', '\\')),
        Path: o.RepoRoot,
        Index: o.RepoIndex,
        Prefix: o.Prefix,
        ColorHex: o.ColorHex,
        Primary: o.IsPrimary);

    /// <summary>The repos in the open workspace, for the <c>list_repos</c> MCP tool.</summary>
    public IReadOnlyList<RepoInfo> BuildRepoList() => _repos;

    /// <summary>
    /// Lints the fleet's C4 mutation-authority graph (one root, one owner per node, acyclic, no overseer
    /// holds a worktree). Empty ⇒ a coherent authority tree. Backs the <c>lint_authority</c> MCP tool so
    /// an orchestrator can check the org chart hasn't gone incoherent as overviews split.
    /// </summary>
    public IReadOnlyList<Styloagent.Core.Architecture.AuthorityViolation> LintAuthority()
    {
        var nodes = Panes
            .Select(p => new Styloagent.Core.Architecture.AuthorityNode(p.Prefix, p.ParentPrefix, p.WorktreePath is not null))
            .ToList();
        return Styloagent.Core.Architecture.AuthorityTreeLint.Check(nodes);
    }

    /// <summary>
    /// Core pane-creation path shared by SpawnProposed and SpawnChild.
    /// Builds the manifest entry, reserves the hook id, creates the AgentPaneViewModel
    /// (with optional lineage), adds it to Panes + dock, and fires SpawnAsync.
    /// Returns null if the dock factory is unavailable (guards are not met).
    /// </summary>
    private AgentPaneViewModel? CreatePaneForProposed(
        ProposedAgent p,
        string? parentPrefix = null,
        int depth = 0,
        string? worktreeOverride = null,
        string? worktreeBranch = null,
        AgentRuntimeKind? runtime = null,
        string? model = null,
        string? effort = null)
    {
        if (_dockFactory is null || _launcher is null || _watcher is null) return null;
        var documentDock = _dockFactory.DocumentDock;
        var rootDock = _dockFactory.RootDock;
        if (documentDock is null || rootDock is null) return null;

        // Persist the launch prompt to a file so the existing LaunchPromptPath path can read it.
        // Anchor a spawn to the SAME repo the overview agent runs in. _project can be null after a
        // restart (no project auto-loaded), so fall back through _repoRoot / STYLOAGENT_REPO before
        // ever dropping to DefaultWorkingDirectory() — which is the user's home (~/), NOT the repo.
        string root = _project?.Root
            ?? _repoRoot
            ?? Environment.GetEnvironmentVariable("STYLOAGENT_REPO")
            ?? DefaultWorkingDirectory();
        string launchPromptPath = string.Empty;
        if (_project is not null && !string.IsNullOrWhiteSpace(p.LaunchPrompt))
        {
            Directory.CreateDirectory(_project.LaunchPromptsDir);
            launchPromptPath = ResolveLaunchPromptPath(_project.LaunchPromptsDir, p.Prefix, p.LaunchPrompt);
            File.WriteAllText(launchPromptPath, p.LaunchPrompt);
        }

        string resolvedWorktree = worktreeOverride ?? WorkingDirectoryResolver.Resolve(
            string.IsNullOrWhiteSpace(p.Dir) ? root : Path.Combine(root, p.Dir),
            root);   // fall back to the repo root, never to ~/

        var entry = new AgentManifestEntry(
            Prefix: p.Prefix,
            Repo: root,
            Worktree: resolvedWorktree,
            LaunchPromptPath: launchPromptPath,
            RestartPromptPath: string.Empty,
            SavedContextPath: SavedContextPathFor(p.Prefix),   // so it can be dehydrated / parked
            Transport: AgentTransport.Local,
            Runtime: runtime ?? _defaultAgentRuntime,
            Model: model,
            Effort: effort);

        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher,
            LaunchArgsFor(hookId, entry));
        var paneVm = new AgentPaneViewModel(
            session,
            entry,
            p.Prefix.TrimEnd('-'),
            PresentationStore.DefaultColorFor(p.Prefix))
        {
            ParentPrefix = parentPrefix,
            Depth = depth,
            Responsibility = p.Responsibility,
            InteractionRecorder = _interaction.RecordInput,
            Host = this,
        };
        paneVm.WorktreePath = worktreeOverride;
        paneVm.WorktreeBranch = worktreeBranch;
        Panes.Add(paneVm);
        SelectedPane = paneVm;
        _panesByHookId[hookId] = paneVm;

        // Federated (BUG 4): a spawned child belongs to the SAME repo instance as its parent, so it must
        // carry the parent's repo tag — otherwise ResolvePtyForRepo / SnapshotLiveAgentsForRepo can't see
        // it and that instance's delivery coordinator never routes to it (only the federated OVERVIEW was
        // tagged, at :1097). An untagged parent is the primary fleet (the default), so its children stay
        // untagged too.
        if (parentPrefix is not null
            && Panes.FirstOrDefault(p => p.Prefix == parentPrefix) is { } parentPane
            && _paneRepoRoot.TryGetValue(parentPane, out var parentRepo))
            _paneRepoRoot[paneVm] = parentRepo;

        // The pane IS a Dock Document — add it directly (Id/Title/CanFloat set in its ctor).
        _dockFactory.AddDockable(documentDock, paneVm);
        _dockFactory.SetActiveDockable(paneVm);
        _dockFactory.SetFocusedDockable(rootDock, paneVm);

        // In a tiled mode, re-tile so the new pane gets its own tile rather than a hidden tab.
        if (LayoutMode != CockpitLayoutMode.Tabs) RebuildCenterLayout();

        _ = paneVm.SpawnAsync();
        PersistRevivalMetadata(entry);
        return paneVm;
    }

    private static string SanitizeFileName(string s)
        => new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    /// <summary>
    /// Picks the file to persist a spawn's launch prompt to WITHOUT clobbering a pre-existing doc. Normally
    /// <c>&lt;prefix&gt;.md</c>; but if that path already holds DIFFERENT content — e.g. an overview-authored
    /// mission doc mistakenly dropped there, or a stale prior prompt — the prompt is written to the reserved
    /// <c>&lt;prefix&gt;.launch.md</c> instead so the existing file survives. The manifest stores whichever path
    /// we return, so the read side follows automatically.
    /// </summary>
    private static string ResolveLaunchPromptPath(string dir, string prefix, string content)
    {
        string primary = Path.Combine(dir, SanitizeFileName(prefix) + ".md");
        try
        {
            if (File.Exists(primary) && File.ReadAllText(primary) != content)
                return Path.Combine(dir, SanitizeFileName(prefix) + ".launch.md");
        }
        catch { return Path.Combine(dir, SanitizeFileName(prefix) + ".launch.md"); }
        return primary;
    }

    /// <summary>Selects a pane so its terminal document is brought to front in the centre dock.</summary>
    [RelayCommand]
    private void SelectPane(AgentPaneViewModel pane)
    {
        SelectedPane = pane;
        ActivateDocumentFor(pane);
    }

    /// <summary>
    /// Single place that re-syncs the Git panel (History + Changes + roster badge) to a pane's
    /// worktree — clearing everything when the pane has no worktree. Fire-and-forget; never blocks.
    /// </summary>
    public void RefreshGitPanelFor(AgentPaneViewModel? pane)
    {
        // The Git panel shows the repo the selected agent works in: its own worktree if it was spawned
        // with one, else the shared project repo. Falling back to the repo root means an existing
        // repository is detected and its history/changes render even before any worktree agent exists
        // (e.g. the overview agent, which shares the main checkout). _repoRoot is set in InitializeAsync
        // — earlier than _project (AttachProject), which is null while the first pane is being selected.
        var gitDir = pane?.WorktreePath ?? _repoRoot ?? _project?.Root;

        // Re-point the live watcher at whichever checkout we're showing (or stop if there's none), OFF the
        // UI thread: FileSystemWatcher.StartRaisingEvents() blocks while it establishes the macOS
        // FSEventStream, and must never run on the dispatcher (it froze the cockpit). Watch() is
        // self-contained, lock-guarded, and marshals its own events back to the UI, so fire-and-forget is
        // safe; the idempotency guard inside Watch() makes the common same-dir re-point a cheap no-op.
        if (_gitWatcher is { } watcher)
            _ = Task.Run(() => watcher.Watch(gitDir));

        if (gitDir is { } path && Directory.Exists(path))
        {
            if (GitGraph is not null) _ = GitGraph.LoadAsync(path);
            if (Changes is not null) _ = Changes.LoadAsync(path);
        }
        else
        {
            GitGraph?.Clear();
            Changes?.Clear();
        }
        if (pane is not null && _git is not null) _ = pane.RefreshGitStatusAsync(_git);
    }

    /// <summary>
    /// Git-op visibility: logs a structured timeline op when the watched worktree switches branch, so an
    /// agent's <c>git checkout</c> is reflected in the cockpit ("switched branch · fix/…") instead of being
    /// invisible. Attributed to the selected agent (whose worktree the watcher tracks).
    /// </summary>
    internal void LogGitBranchChange(string? branch)
    {
        var pane = SelectedPane;
        string who   = pane?.DisplayName ?? "git";
        string color = pane?.BorderColorHex ?? "#8899BB";
        string desc  = string.IsNullOrEmpty(branch) ? "detached HEAD" : $"switched branch · {branch}";
        Timeline.Add(DateTimeOffset.Now, who, desc, color);
    }

    /// <summary>Keeps each pane's <see cref="AgentPaneViewModel.IsSelected"/> in sync so the roster
    /// outlines only the active agent. Also refreshes the Git panel for the new pane.</summary>
    partial void OnSelectedPaneChanged(AgentPaneViewModel? oldValue, AgentPaneViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(GitSidebarTitle));
        RefreshGitPanelFor(newValue);
    }

    /// <summary>Brings the dock document for <paramref name="pane"/> to the front. The pane IS a
    /// Dock Document, so it is the dockable itself.</summary>
    private void ActivateDocumentFor(AgentPaneViewModel pane)
    {
        if (_dockFactory is null) return;
        // Reopen a closed/hidden pane: if it isn't currently in the dock tree (closing the tab or Hide
        // removed the dockable — the session kept running), add it back so a roster click always brings the
        // agent's terminal on screen.
        bool docked = pane.Owner is global::Dock.Model.Core.IDock d && d.VisibleDockables?.Contains(pane) == true;
        if (!docked && _dockFactory.DocumentDock is { } dock)
        {
            pane.IsHidden = false;
            _dockFactory.AddDockable(dock, pane);
        }
        _dockFactory.SetActiveDockable(pane);
    }

    /// <summary>Reverse sync: activating a dock tab updates the roster selection/highlight.</summary>
    private void OnActiveDockableChanged(object? sender, global::Dock.Model.Core.Events.ActiveDockableChangedEventArgs e)
    {
        if (e.Dockable is AgentPaneViewModel pane && !ReferenceEquals(SelectedPane, pane))
            SelectedPane = pane;
    }

    // ── Hook state channel (§4.4) ─────────────────────────────────────────────

    /// <summary>
    /// Reserves a unique, filename-safe hook id for a pane, derived from its prefix.
    /// De-duplicates so two agents whose prefixes sanitize to the same token stay distinct.
    /// </summary>
    private string ReserveHookId(string prefix)
    {
        string baseId = HookSettings.SanitizeAgentId(prefix);
        string id = baseId;
        int n = 1;
        while (_panesByHookId.ContainsKey(id))
            id = $"{baseId}-{n++}";
        _panesByHookId[id] = null!; // reserve the slot; the real pane is set by the caller
        return id;
    }

    /// <summary>
    /// Ensures a channel fleet agent can revive from its OWN saved-context doc: if it has a saved-context
    /// doc but no restart prompt (the channel's launch-prompts were consumed/absent), generate one from
    /// HydrationText (identity + re-read your context doc + inbox + stay in scope) and wire it as the
    /// agent's launch/restart prompt — the same treatment the overview gets. Otherwise the agent is
    /// returned unchanged. Best-effort; a write failure falls back to the default "begin your work" prompt.
    /// </summary>
    private static AgentManifestEntry EnsureRevivePrompt(AgentManifestEntry e, string channelRoot)
    {
        if (string.IsNullOrWhiteSpace(e.SavedContextPath) || !string.IsNullOrWhiteSpace(e.LaunchPromptPath))
            return e;
        try
        {
            var dir = Path.Combine(channelRoot, "launch-prompts");
            Directory.CreateDirectory(dir);
            var restart = Path.Combine(dir, $"{e.Prefix}restart.md");
            if (!File.Exists(restart))
                File.WriteAllText(restart, Styloagent.Core.Hooks.HydrationText.For(
                    e.Prefix, e.SavedContextPath,
                    Path.Combine(channelRoot, "PROTOCOL.md"), channelRoot));
            return e with { LaunchPromptPath = restart, RestartPromptPath = restart };
        }
        catch { return e; }
    }

    // Runtime identity is part of revival metadata. Saved-context files deliberately remain portable and
    // runtime-neutral, while this sidecar records which CLI owns each parked context in this channel.
    private static string RevivalManifestPath(string channelRoot) => Path.Combine(channelRoot, "agents.yaml");

    private static async Task<IReadOnlyDictionary<string, AgentRuntimeKind>> LoadPersistedRuntimesAsync(string channelRoot)
    {
        var path = RevivalManifestPath(channelRoot);
        if (!File.Exists(path)) return new Dictionary<string, AgentRuntimeKind>();

        try
        {
            var entries = await new ManifestStore().LoadAsync(path);
            return entries.ToDictionary(e => e.Prefix, e => e.Runtime, StringComparer.Ordinal);
        }
        catch
        {
            // A damaged optional sidecar must not prevent the channel from reopening; it simply falls back
            // to the current startup default for entries without persisted runtime metadata.
            return new Dictionary<string, AgentRuntimeKind>();
        }
    }

    private void PersistRevivalMetadata(AgentManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_channelRoot)) return;
        var path = RevivalManifestPath(_channelRoot);
        _ = Task.Run(async () =>
        {
            try
            {
                var store = new ManifestStore();
                var existing = File.Exists(path) ? await store.LoadAsync(path) : Array.Empty<AgentManifestEntry>();
                var entries = existing.Where(e => e.Prefix != entry.Prefix).Append(entry).ToList();
                await store.SaveAsync(path, entries);
            }
            catch
            {
                // Persistence is best-effort; a live agent must never be held back by a metadata write.
            }
        });
    }

    /// <summary>The <c>--settings</c> hook args for a hook id, or none if the channel is unavailable.</summary>
    private IReadOnlyList<string> HookArgs(string hookId)
        => _hookChannel?.SettingsArgsFor(hookId) ?? Array.Empty<string>();

    /// <summary>The fleet permission mode agents launch with (from Settings; default Scoped so agents can
    /// coordinate + edit without a prompt per action).</summary>
    public Styloagent.Core.Hooks.FleetPermissionMode PermissionMode => SelectedPermissionMode;

    /// <summary>
    /// Hook args PLUS the compaction guard (re-inject hydration on compact/resume) PLUS the permission mode
    /// (Scoped/Bypass) so an agent can actually act without a human approving every tool use.
    /// </summary>
    private IReadOnlyList<string> HookArgs(string hookId, AgentManifestEntry entry)
        => HookArgs(hookId, entry, _hookChannel, _channelRoot, _repoRoot, _project?.ProtocolPath);

    /// <summary>
    /// Runtime-aware launch args. Claude gets today's full settings hook/MCP/system-prompt contract.
    /// Codex gets Codex-native hook <c>--config</c> args and permission flags, without Claude-only CLI flags.
    /// </summary>
    private IReadOnlyList<string> LaunchArgsFor(string hookId, AgentManifestEntry entry,
        IEnumerable<string>? claudeOnlyArgs = null)
        => LaunchArgsFor(hookId, entry, _hookChannel, _channelRoot, _repoRoot, _project?.ProtocolPath, claudeOnlyArgs);

    /// <summary>
    /// As <see cref="LaunchArgsFor(string, AgentManifestEntry, IEnumerable{string}?)"/> but against a
    /// specific hook channel and repo context for federated repo instances.
    /// </summary>
    private IReadOnlyList<string> LaunchArgsFor(
        string hookId, AgentManifestEntry entry, HookChannel? hooks, string? channelRoot, string? repoRoot,
        string? protocolPath, IEnumerable<string>? claudeOnlyArgs = null)
    {
        var runtime = AgentRuntimeProfile.For(entry.Runtime);
        var selectionArgs = ModelEffortArgs(entry);
        if (entry.Runtime == AgentRuntimeKind.Codex)
        {
            var args = new List<string>();
            args.AddRange(selectionArgs);
            if (hooks is not null)
            {
                var hydration = Styloagent.Core.Hooks.HydrationText.For(
                    entry.Prefix,
                    string.IsNullOrWhiteSpace(entry.SavedContextPath) ? null : entry.SavedContextPath,
                    protocolPath,
                    channelRoot);
                var file = hooks.WriteHydrationFile(hookId, hydration);
                args.AddRange(Styloagent.Core.Hooks.CodexHookSettings.BuildConfigArgs(
                    hookId, hooks.HooksDirectory, file,
                    Styloagent.Core.Hooks.HookSettings.DefaultGateInvocation(), repoRoot, entry.Prefix));
            }
            args.AddRange(CodexDeveloperInstructionArgs(claudeOnlyArgs));
            args.AddRange(CodexMcpArgsFor(entry.Prefix));
            args.AddRange(runtime.PermissionArgs(PermissionMode));
            return args;
        }

        if (!runtime.SupportsClaudeSettingsHooks)
            return runtime.PermissionArgs(PermissionMode);

        return HookArgs(hookId, entry, hooks, channelRoot, repoRoot, protocolPath)
            .Concat(claudeOnlyArgs ?? Array.Empty<string>())
            .Concat(McpArgsFor(entry.Prefix))
            .Concat(selectionArgs)
            .ToArray();
    }

    private static IReadOnlyList<string> ModelEffortArgs(AgentManifestEntry entry)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Model))
        {
            args.Add("--model");
            args.Add(entry.Model!);
        }
        if (!string.IsNullOrWhiteSpace(entry.Effort) &&
            !entry.Effort.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            if (entry.Runtime == AgentRuntimeKind.Codex)
            {
                args.Add("--config");
                args.Add($"model_reasoning_effort={TomlString(entry.Effort!)}");
            }
            else
            {
                args.Add("--effort");
                args.Add(entry.Effort!);
            }
        }
        return args;
    }

    /// <summary>
    /// As <see cref="HookArgs(string, AgentManifestEntry)"/> but against a SPECIFIC hook channel + channel/repo
    /// roots — so a federated repo instance's agents route their hooks to that instance's OWN hooks dir (its
    /// own delivery + PickedUp + badges) and hydrate from its own channel + PROTOCOL, never the primary's.
    /// </summary>
    private IReadOnlyList<string> HookArgs(
        string hookId, AgentManifestEntry entry, HookChannel? hooks, string? channelRoot, string? repoRoot,
        string? protocolPath)
    {
        if (hooks is null) return Styloagent.Core.Hooks.HookSettings.PermissionArgs(PermissionMode);
        var hydration = Styloagent.Core.Hooks.HydrationText.For(
            entry.Prefix,
            string.IsNullOrWhiteSpace(entry.SavedContextPath) ? null : entry.SavedContextPath,
            protocolPath,
            channelRoot);
        var file = hooks.WriteHydrationFile(hookId, hydration);
        // Ownership PreToolUse gate: pass the gate-mode invocation + the repo root + the OWNERSHIP PREFIX as
        // caller (entry.Prefix, NOT hookId — ReserveHookId may suffix hookId to e.g. "session--1"). The gate
        // enforces main-sharing agents against ownership.yaml; worktree agents edit outside _repoRoot ⇒
        // unowned ⇒ allow (safe no-op in v1). Wired here by overview- (coordination-root bypass) per the
        // ownership-enforcement design; the gate logic itself is session-'s Core/Hooks work.
        return hooks.SettingsArgsFor(hookId, file, PermissionMode,
                Styloagent.Core.Hooks.HookSettings.DefaultGateInvocation(), repoRoot, entry.Prefix)
            .Concat(Styloagent.Core.Hooks.HookSettings.PermissionArgs(PermissionMode))
            .ToList();
    }

    private AgentRuntimeKind RuntimeFromRequest(string? runtime)
        => string.Equals(runtime, "codex", StringComparison.OrdinalIgnoreCase)
            ? AgentRuntimeKind.Codex
            : string.Equals(runtime, "claude", StringComparison.OrdinalIgnoreCase)
                ? AgentRuntimeKind.Claude
                : _defaultAgentRuntime;

    private static string RuntimeName(AgentRuntimeKind runtime)
        => runtime == AgentRuntimeKind.Codex ? "codex" : "claude";

    private static IReadOnlyList<string> CodexDeveloperInstructionArgs(IEnumerable<string>? claudeOnlyArgs)
    {
        if (claudeOnlyArgs is null) return Array.Empty<string>();
        var args = claudeOnlyArgs.ToList();
        var prompts = new List<string>();
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--append-system-prompt" && !string.IsNullOrWhiteSpace(args[i + 1]))
                prompts.Add(args[i + 1]);
        }
        if (prompts.Count == 0) return Array.Empty<string>();
        return new[] { "--config", $"developer_instructions={TomlString(string.Join("\n\n", prompts))}" };
    }

    private static string TomlString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Routes a hook event (raised on a background thread) to the owning pane on the UI thread.</summary>
    private void OnHookEvent(HookEvent e)
    {
        Dispatcher.UIThread.Post(() => ApplyHookEventOnUiThread(e));
    }

    /// <summary>Applies a hook event on the UI thread — extracted so tests can call it directly.</summary>
    private void ApplyHookEventOnUiThread(HookEvent e)
    {
        if (_panesByHookId.TryGetValue(e.AgentId, out var pane) && pane is not null)
        {
            // PreCompact fallback: persist a best-effort resume anchor right before a compaction, then stop —
            // it's not an interaction/state change, so skip the needs-you + delivery bookkeeping below.
            if (e.EventName == "PreCompact")
            {
                _ = WriteInPlaceCheckpointAsync(pane);
                return;
            }

            pane.ApplyHookEvent(e);
            // A throttled agent fires no hooks; ANY hook event means it made forward progress → clear the
            // throttle even if no fresh output arrived (the detector no-ops if it wasn't throttled).
            if (_throttle.TryGetValue(pane, out var t)) t.Detector.NoteResumed(DateTimeOffset.UtcNow);
            pane.WaitingSince = pane.NeedsYou ? (pane.WaitingSince ?? DateTimeOffset.UtcNow) : null;
            RecordTimelineFromHook(pane, e);   // add the timeline entry BEFORE the instrument refresh
            RefreshAttention();                 // (which reads TimelineCount)
            if (!_interaction.IsBusy(IdleWindow)) AutoRevealHead();

            // When the agent goes idle, flush any NextPrompt messages that were deferred for it.
            if (_deliveryService is not null)
                _ = _deliveryService.OnRecipientStateChangedAsync(pane.Prefix, pane.HookState);
        }
    }

    /// <summary>Maps a hook event to an activity-timeline entry (skips the high-frequency events).</summary>
    private void RecordTimelineFromHook(AgentPaneViewModel pane, HookEvent e)
    {
        string? desc = e.EventName switch
        {
            "SessionStart" => "came online",
            "SessionEnd"   => "exited",
            "PreToolUse"   => DescribeOp(e.ToolName, e.ToolTarget),
            "Notification" => e.NotificationType switch
            {
                "permission_prompt" or "agent_needs_input" or "elicitation_dialog" => "needs you",
                "idle_prompt" => "went idle",
                _ => null,
            },
            _ => null,   // UserPromptSubmit / PostToolUse / Stop — too frequent for the timeline
        };
        // A file the row can open: only for the file-touching tools, and only a real path.
        string? path = e.EventName == "PreToolUse"
            && e.ToolName is "Read" or "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "NotebookRead"
            && !string.IsNullOrWhiteSpace(e.ToolTarget) && e.ToolTarget.Contains('/')
                ? e.ToolTarget : null;

        // Remember who last touched this file, for coordination context.
        if (path is not null)
            RecordFileTouch(path, pane.DisplayName, HookActivity.DescribeTool(e.ToolName));

        // An Edit carries a before/after → the row opens a diff instead of the whole file.
        string? diffOld = null, diffNew = null;
        if (e.EventName == "PreToolUse" && e.ToolName == "Edit" && e.ToolOld is not null && e.ToolNew is not null)
            (diffOld, diffNew) = (e.ToolOld, e.ToolNew);

        if (!string.IsNullOrEmpty(desc))
            Timeline.Add(DateTimeOffset.Now, pane.DisplayName, desc, pane.BorderColorHex, path, diffOld, diffNew);
    }

    /// <summary>Formats a tool operation with its target — "editing · Foo.cs", "running commands · git status".</summary>
    private static string DescribeOp(string? tool, string? target)
    {
        var verb = HookActivity.DescribeTool(tool);
        if (string.IsNullOrEmpty(verb) || string.IsNullOrWhiteSpace(target)) return verb;

        var t = target.Trim();
        bool isFile = tool is "Read" or "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "NotebookRead";
        if (isFile && t.Contains('/')) t = Path.GetFileName(t);
        if (t.Length > 44) t = t[..44] + "…";
        return $"{verb} · {t}";
    }

    /// <summary>
    /// Opens a file in a read-only, syntax-highlighted source document tab (from a timeline click).
    /// Added to the centre dock like a markdown doc; no-op for a blank path.
    /// </summary>
    public void OpenSourceDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _dockFactory?.DocumentDock is null || _dockFactory.RootDock is null)
            return;

        var doc = new SourceDocumentViewModel(path) { Id = "Src-" + Guid.NewGuid().ToString("N"), CanFloat = true };
        _dockFactory.AddDockable(_dockFactory.DocumentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(_dockFactory.RootDock, doc);
    }

    /// <summary>
    /// Viewer-by-type dispatch for the document surface: markdown files (<c>.md</c>/<c>.markdown</c>) get
    /// the rendered-markdown viewer, everything else the read-only source viewer. Pure so it is unit-testable
    /// without a dock.
    /// </summary>
    internal static DocViewerKind ViewerKindForPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".md" or ".markdown") return DocViewerKind.Markdown;
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff"
            ? DocViewerKind.Image : DocViewerKind.Source;
    }

    /// <summary>
    /// Opens a file as the dock document that matches its type (see <see cref="ViewerKindForPath"/>). This is
    /// the single open path shared by the top-bar document search and the drag-onto-the-doc-surface drop, so
    /// they behave identically. No-ops for a blank path or before the dock is initialised.
    /// </summary>
    /// <summary>Handle an agent's <c>open_document</c> request: note who surfaced it (and why) on the
    /// timeline, then open the file on the doc surface. The Core verb already canonicalized the path,
    /// scope-checked it against an open repo, and confirmed it exists — so we just open it.</summary>
    private void HandleDocumentOpen(Styloagent.Core.Attention.DocumentOpenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Path)) return;
        var who = string.IsNullOrWhiteSpace(req.AskingPrefix) ? "agent" : req.AskingPrefix.TrimEnd('-');
        var why = string.IsNullOrWhiteSpace(req.Reason) ? "" : $": {req.Reason}";
        Timeline.Add(DateTimeOffset.Now, who, $"opened {Path.GetFileName(req.Path)}{why}", "#8899BB");
        OpenDocumentByPath(req.Path);
    }

    public void OpenDocumentByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        switch (ViewerKindForPath(path))
        {
            case DocViewerKind.Markdown:
                OpenMarkdownDocument(new MarkdownDocumentViewModel(Path.GetFileNameWithoutExtension(path), path));
                break;
            case DocViewerKind.Image:
                OpenImageDocument(new ImageDocumentViewModel(Path.GetFileNameWithoutExtension(path), path));
                break;
            default:
                OpenSourceDocument(path);
                break;
        }
    }

    private void OpenImageDocument(ImageDocumentViewModel doc)
    {
        if (_dockFactory?.DocumentDock is null || _dockFactory.RootDock is null) return;
        doc.Id = "Img-" + Guid.NewGuid().ToString("N");
        doc.CanFloat = true;
        _dockFactory.AddDockable(_dockFactory.DocumentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(_dockFactory.RootDock, doc);
    }

    /// <summary>
    /// The per-agent log file path: <c>&lt;root&gt;/.styloagent/logs/&lt;prefix&gt;.md</c> — the sidecar
    /// session- appends and repo- indexes (agent-log design). Pure so it is unit-testable without a project.
    /// </summary>
    internal static string AgentLogPathFor(string root, string prefix)
        => Path.Combine(root, ".styloagent", "logs", prefix + ".md");

    /// <summary>
    /// Opens an agent's durable log (<c>.styloagent/logs/&lt;prefix&gt;.md</c>) as a rendered-markdown
    /// document — the same open-as-rendered-markdown gesture every markdown surface uses. Scoped to the
    /// given prefix (the selected agent). No-ops before a project is attached.
    /// </summary>
    public void OpenAgentLog(string prefix)
    {
        if (_project is null || string.IsNullOrEmpty(prefix)) return;
        OpenDocumentByPath(AgentLogPathFor(_project.Root, prefix));
    }

    /// <summary>Opens an edit's before/after as a highlighted line-diff document (from a timeline click).</summary>
    public void OpenDiffDocument(TimelineEntry entry)
    {
        if (entry.DiffOld is null || entry.DiffNew is null
            || _dockFactory?.DocumentDock is null || _dockFactory.RootDock is null)
            return;

        var title = string.IsNullOrWhiteSpace(entry.Path) ? "diff" : Path.GetFileName(entry.Path);
        var doc = new DiffDocumentViewModel(title, entry.DiffOld, entry.DiffNew)
        {
            Id = "Diff-" + Guid.NewGuid().ToString("N"),
            CanFloat = true,
        };
        _dockFactory.AddDockable(_dockFactory.DocumentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(_dockFactory.RootDock, doc);
    }

    /// <summary>Resolves an agent id (pane prefix) to its live PTY for message injection.</summary>
    private IPtySession? ResolvePty(string agentId)
    {
        try { return Panes.FirstOrDefault(p => p.Prefix == agentId)?.CurrentPty; }
        catch (InvalidOperationException) { return null; }  // panes changed mid-lookup
    }

    /// <summary>Resolves an agent id to its live PTY WITHIN one federated repo instance. Under the
    /// <c>(repoRoot, prefix)</c> model the same prefix can exist in two repos, so an instance's injector must
    /// scope to its own panes — otherwise a nudge could type into the primary's same-named agent.</summary>
    /// <summary>Test seam: the repo-instance root a pane is tagged with (federated PTY/delivery routing),
    /// or null when untagged (the primary fleet). Mirrors what ResolvePtyForRepo / SnapshotLiveAgentsForRepo
    /// key off, so a test can assert a spawned federated child inherited its parent's repo tag (BUG 4).</summary>
    internal string? RepoRootForPaneForTest(string prefix)
        => Panes.FirstOrDefault(p => p.Prefix == prefix) is { } pane
           && _paneRepoRoot.TryGetValue(pane, out var root) ? root : null;

    private IPtySession? ResolvePtyForRepo(string repoRoot, string agentId)
    {
        try
        {
            return Panes.FirstOrDefault(p => p.Prefix == agentId
                    && _paneRepoRoot.TryGetValue(p, out var r)
                    && string.Equals(r, repoRoot, StringComparison.OrdinalIgnoreCase))
                ?.CurrentPty;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Snapshots the live agents (prefix + hook state) for delivery routing. Best-effort:
    /// retries on concurrent pane mutation, then yields empty rather than throwing into a reload.</summary>
    private IReadOnlyList<AgentPresence> SnapshotLiveAgents()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return Panes.Select(p => new AgentPresence(p.Prefix, p.HookState)).ToList(); }
            catch (InvalidOperationException) { /* collection changed mid-enumeration; retry */ }
        }
        return Array.Empty<AgentPresence>();
    }

    /// <summary>Live agents belonging to ONE federated repo instance (tagged by its repoRoot) — so that
    /// instance's delivery coordinator only nudges its own agents, never the primary fleet's.</summary>
    private IReadOnlyList<AgentPresence> SnapshotLiveAgentsForRepo(string repoRoot)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Panes
                    .Where(p => _paneRepoRoot.TryGetValue(p, out var r)
                                && string.Equals(r, repoRoot, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new AgentPresence(p.Prefix, p.HookState))
                    .ToList();
            }
            catch (InvalidOperationException) { /* collection changed mid-enumeration; retry */ }
        }
        return Array.Empty<AgentPresence>();
    }

    /// <summary>
    /// Syncs each pane's <see cref="AgentPaneViewModel.PendingOperatorQuestion"/> marker to the operator-
    /// question hub's current pending set (fired on PendingChanged, on the UI thread). Kept off HookState so
    /// the delivery service's view of the recipient stays honest.
    /// </summary>
    private void ReconcileOperatorQuestionPanes()
        => ReconcilePaneQuestions(Panes, OperatorQuestions?.PendingByPrefix ?? EmptyOperatorQuestions);

    private static readonly IReadOnlyDictionary<string, string> EmptyOperatorQuestions
        = new Dictionary<string, string>();

    /// <summary>Pure reconcile: set each pane's pending-question marker to its question, or clear it. Testable.</summary>
    internal static void ReconcilePaneQuestions(
        IEnumerable<AgentPaneViewModel> panes, IReadOnlyDictionary<string, string> pendingByPrefix)
    {
        foreach (var pane in panes)
            pane.PendingOperatorQuestion = pendingByPrefix.TryGetValue(pane.Prefix, out var q) ? q : "";
    }

    /// <summary>Rebuilds the oldest-first attention queue from the current panes.</summary>
    public void RefreshAttention()
    {
        var order = Styloagent.Core.Attention.AttentionQueue.Build(
            Panes.Select(p => new Styloagent.Core.Attention.AttentionCandidate(p.Prefix, p.NeedsYou, p.WaitingSince)));
        AttentionQueue.Clear();
        foreach (var id in order)
        {
            var pane = Panes.FirstOrDefault(p => p.Prefix == id);
            if (pane is not null) AttentionQueue.Add(pane);
        }
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(AttentionHudText));
        RefreshInstruments();
    }

    // ── Attention reveal + jump (Task 4) ─────────────────────────────────────

    /// <summary>
    /// Makes a pane's tab visible. When <paramref name="focus"/> is true, also grabs keyboard
    /// focus — only for human-initiated jumps. Auto-reveal passes false to honour the focus
    /// invariant (no keyboard grab without the human asking).
    /// </summary>
    public void RevealPane(AgentPaneViewModel pane, bool focus)
    {
        if (_dockFactory is null) return;
        // The pane IS the dockable (it inherits Document).
        _dockFactory.SetActiveDockable(pane);

        if (focus)
        {
            if (_dockFactory.RootDock is { } rootDock)
                _dockFactory.SetFocusedDockable(rootDock, pane);
            SelectedPane = pane;
            JumpFocusCountForTest++;
        }
        else
        {
            AutoActivateCountForTest++;
        }
    }

    /// <summary>
    /// Called on each idle-timer tick (~1 s). Surfaces the head waiter when the human
    /// has been quiet for at least <see cref="IdleWindow"/> — this is trigger (b): a pane
    /// that was queued while the human was busy is revealed as soon as they go idle.
    /// </summary>
    private int _usageTick;

    internal void OnIdleTick()
    {
        // Refresh each roster's relative "last output Ns" readout off the shared 1s tick
        // (no per-pane timer). Runs on the UI thread; panes only mutate here too.
        foreach (var pane in Panes) pane.TickRelativeTimes();

        // Every ~3s, refresh each agent's token/context readout from its transcript (off-thread),
        // then run the scope-dilution guard off the freshest readings.
        if (++_usageTick % 3 == 0)
        {
            foreach (var pane in Panes) pane.RefreshUsage();
            CheckContextDilution();
            // Feed the 0.80 checkpoint monitor off the fresh readings — fires CheckpointNeeded once per
            // fill-up (re-arms after a compaction), so it's safe to observe every tick.
            foreach (var pane in Panes)
                if (pane.State == SessionState.Live)
                    _checkpointMonitor.Observe(pane.Prefix, pane.ContextFraction);
        }

        if (!_interaction.IsBusy(IdleWindow)) AutoRevealHead();
    }

    /// <summary>Auto-reveals the oldest waiting pane iff the human is idle and it is not already active.</summary>
    public void AutoRevealHead()
    {
        var head = AttentionQueue.FirstOrDefault();
        var target = AutoReveal.Decide(_interaction.IsBusy(IdleWindow), head?.Prefix, ActivePrefix());
        if (target is not null && head is not null) RevealPane(head, focus: false);
    }

    /// <summary>Jumps keyboard focus to the oldest waiting pane (human-initiated; always focuses).</summary>
    [RelayCommand]
    private void JumpToNextWaiting()
    {
        var head = AttentionQueue.FirstOrDefault();
        if (head is not null) RevealPane(head, focus: true);
    }

    /// <summary>Returns the prefix of the currently active document, or null when nothing is active.</summary>
    private string? ActivePrefix()
    {
        if (_dockFactory?.DocumentDock?.ActiveDockable is AgentPaneViewModel p)
            return p.Prefix;
        return null;
    }

    /// <summary>Exposes the interaction monitor for tests.</summary>
    internal InteractionMonitor InteractionForTest() => _interaction;

    /// <summary>Exposes the active-prefix logic for tests.</summary>
    internal string? ActivePrefixForTest() => ActivePrefix();

    /// <summary>Drives an open_document request through the same handler the DocumentOpenHub fires (for
    /// tests — bypasses the MCP hub + UIThread.Post).</summary>
    internal void RaiseDocumentOpenForTest(Styloagent.Core.Attention.DocumentOpenRequest req)
        => HandleDocumentOpen(req);

    /// <summary>Returns the first hook id registered (for test seams).</summary>
    internal string FirstHookIdForTest()
        => _panesByHookId.Keys.First();

    /// <summary>Returns the second hook id registered (for test seams that need a background pane).</summary>
    internal string SecondHookIdForTest()
        => _panesByHookId.Keys.Skip(1).First();

    /// <summary>Applies a hook event synchronously on the calling thread (for unit tests — bypasses UIThread.Post).</summary>
    internal void DispatchHookForTest(HookEvent e) => ApplyHookEventOnUiThread(e);

    /// <summary>Runs one delivery pass over the channel (for tests — the real path fires this on bus reload).</summary>
    internal Task<int> PumpDeliveryForTest(CancellationToken ct = default)
        => _deliveryCoordinator?.PumpAsync(ct) ?? Task.FromResult(0);

    /// <summary>
    /// The default directory to launch agents in when their worktree isn't configured.
    /// Overridable via the STYLOAGENT_WORKDIR environment variable; defaults to the
    /// user's home directory.
    /// </summary>
    private static string DefaultWorkingDirectory()
    {
        var env = Environment.GetEnvironmentVariable("STYLOAGENT_WORKDIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// Returns the entry with a valid working directory — its worktree if that exists,
    /// otherwise the app default — so the spawn is never handed an empty Cwd.
    /// </summary>
    private static AgentManifestEntry WithWorkingDir(AgentManifestEntry e)
        => e with { Worktree = WorkingDirectoryResolver.Resolve(e.Worktree, DefaultWorkingDirectory()) };

    /// <summary>Builds an agent manifest entry from a detected git worktree.</summary>
    private static AgentManifestEntry WorktreeEntry(GitWorktree w, string repoRoot)
        => new(
            Prefix: w.Name + "-",
            Repo: repoRoot,
            Worktree: w.Path,
            LaunchPromptPath: string.Empty,
            RestartPromptPath: string.Empty,
            SavedContextPath: string.Empty,
            Transport: AgentTransport.Local);

    /// <summary>Exposes the center DocumentDock for direct inspection (e.g. tests).</summary>
    public DocumentDock? DocumentDock => _dockFactory?.DocumentDock;

    /// <summary>Exposes the live bus feed view-model (e.g. tests).</summary>
    public BusViewModel? BusViewModel => _busViewModel;

    /// <summary>Opens a bus message's backing markdown <c>.md</c> file as a document (double-click a message).</summary>
    public void OpenBusMessageDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        OpenMarkdownDocument(new MarkdownDocumentViewModel(Path.GetFileNameWithoutExtension(path), path));
    }

    /// <summary>Opens a bus thread as a carousel document — page through all its messages rendered as markdown.</summary>
    public void OpenBusThreadDocument(BusThreadItem thread)
    {
        if (_dockFactory?.DocumentDock is null || _dockFactory.RootDock is null) return;
        var doc = new BusThreadDocumentViewModel(thread);
        _dockFactory.AddDockable(_dockFactory.DocumentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(_dockFactory.RootDock, doc);
    }

    /// <summary>
    /// Opens a markdown document in a new floating dockable tab in the centre DocumentDock.
    /// No-ops when the dock factory is not yet initialised (e.g. empty-entries path).
    /// </summary>
    public void OpenMarkdownDocument(MarkdownDocumentViewModel docVm)
    {
        if (_dockFactory is null)
            return;

        var documentDock = _dockFactory.DocumentDock;
        var rootDock = _dockFactory.RootDock;
        if (documentDock is null || rootDock is null)
            return;

        // The markdown/diagram VM IS a Dock Document — add it directly. Give it a unique dock Id so
        // opening the same-titled doc twice doesn't collide.
        docVm.Id = "Doc-" + Guid.NewGuid().ToString("N");
        docVm.CanFloat = true;
        // Clicking a C4 component in an architecture doc focuses its owning agent.
        docVm.ComponentClicked -= FocusAgentByComponentId;
        docVm.ComponentClicked += FocusAgentByComponentId;
        _dockFactory.AddDockable(documentDock, docVm);
        _dockFactory.SetActiveDockable(docVm);
        _dockFactory.SetFocusedDockable(rootDock, docVm);
    }

    /// <summary>
    /// Focuses the agent that owns a clicked C4 component. C4ResponsibilityGenerator uses the
    /// sanitized agent prefix as the element id, so match panes on that.
    /// </summary>
    private void FocusAgentByComponentId(string componentId)
    {
        var pane = Panes.FirstOrDefault(p => SystemMapGenerator.Id(p.Prefix) == componentId);
        if (pane is null) return;
        SelectedPane = pane;
        ActivateDocumentFor(pane);
    }

    // ── Diagram cockpit commands ──────────────────────────────────────────────

    /// <summary>Opens a live System Map diagram tab driven by the current fleet roster.</summary>
    [RelayCommand]
    private void ShowSystemMap()
    {
        var doc = new DiagramDocumentViewModel(
            "System Map",
            DiagramKind.SystemMap,
            () => SystemMapGenerator.Build(BuildFleetNodes()));
        _openDiagrams.Add(doc);
        OpenMarkdownDocument(doc);
    }

    /// <summary>Opens a live Bus Sequence diagram tab driven by the current bus threads.</summary>
    [RelayCommand]
    private void ShowBusSequence()
    {
        var doc = new DiagramDocumentViewModel(
            "Bus Sequence",
            DiagramKind.BusSequence,
            () => BusSequenceGenerator.Build(BuildBusThreads()));
        _openDiagrams.Add(doc);
        OpenMarkdownDocument(doc);
    }

    /// <summary>Builds the fleet-node list for the System Map from the current pane roster.</summary>
    internal IReadOnlyList<FleetNode> BuildFleetNodes()
        => Panes
            .Select(p => new FleetNode(p.Prefix, p.ParentPrefix, p.Responsibility, p.HookStateText ?? ""))
            .ToList();

    /// <summary>Opens a live, ownership-coloured, clickable C4 architecture view of the fleet's
    /// responsibility decomposition — each agent a component in its own identity colour.</summary>
    [RelayCommand]
    private void ShowArchitecture()
    {
        var doc = new DiagramDocumentViewModel(
            "Architecture",
            DiagramKind.Architecture,
            () => C4ResponsibilityGenerator.Build(BuildArchitectureComponents(), BuildArchitectureLinks(), "Responsibility"));
        _openDiagrams.Add(doc);
        OpenMarkdownDocument(doc);
    }

    internal IReadOnlyList<ArchitectureComponent> BuildArchitectureComponents()
        => Panes
            .Select(p => new ArchitectureComponent(p.Prefix, p.DisplayName, p.Responsibility, p.BorderColorHex))
            .ToList();

    internal IReadOnlyList<ArchitectureLink> BuildArchitectureLinks()
        => Panes
            .Where(p => !string.IsNullOrWhiteSpace(p.ParentPrefix))
            .Select(p => new ArchitectureLink(p.ParentPrefix!, p.Prefix, "spawned"))
            .ToList();

    /// <summary>
    /// Builds the bus-thread list for the Bus Sequence diagram from <see cref="BusViewModel"/>.
    /// Each <c>BusThreadItem</c> has no slug property; the slug is derived from the first
    /// message's <c>Slug</c> when available, falling back to the thread's <c>Subject</c>.
    /// </summary>
    internal IReadOnlyList<SeqThread> BuildBusThreads()
    {
        if (_busViewModel is null) return Array.Empty<SeqThread>();

        return _busViewModel.AttentionThreads
            .Concat(_busViewModel.RecentThreads)
            .Concat(_busViewModel.ArchivedThreads)
            .Select(item =>
            {
                string slug = item.Messages.Count > 0
                    ? item.Messages[0].Slug
                    : item.Subject;
                var messages = item.Messages
                    .Select(m => new SeqMessage(m.From ?? "?", m.Timestamp))
                    .ToList();
                return new SeqThread(slug, messages);
            })
            .ToList();
    }

    /// <summary>Exposes the open-diagrams list for test assertions.</summary>
    internal IReadOnlyList<DiagramDocumentViewModel> OpenDiagramsForTest() => _openDiagrams;

    /// <summary>Deterministic test seam: runs live-diagram regeneration synchronously.</summary>
    internal void RegenerateLiveDiagramsForTest() => RegenerateLiveDiagrams();

    /// <summary>Regenerates every tracked diagram whose <see cref="DiagramDocumentViewModel.Live"/> flag is true.</summary>
    private void RegenerateLiveDiagrams()
    {
        foreach (var d in _openDiagrams)
        {
            if (d.Live) d.RegenerateCommand.Execute(null);
        }
    }

    /// <summary>
    /// Arms (or re-arms) the 500 ms single-shot debounce timer for live diagram regeneration.
    /// Safe to call from any thread that can reach the UI dispatcher.
    /// Note: bus-data changes are not currently wired here — only Panes changes trigger the
    /// debounce. Hooking BusViewModel.PropertyChanged is a future improvement.
    /// </summary>
    private void ArmDiagramDebounce()
    {
        if (_diagramDebounceTimer is null)
        {
            // Create on first call (ctor runs before Dispatcher is available in tests).
            _diagramDebounceTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(500),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _diagramDebounceTimer?.Stop();
                    RegenerateLiveDiagrams();
                });
        }

        _diagramDebounceTimer.Stop();
        _diagramDebounceTimer.Start();
    }

    /// <summary>Ensures .worktrees/ is git-ignored via .git/info/exclude (never touches the user's .gitignore).</summary>
    /// <summary>
    /// Materialises the cross-repo lucidview checkout under <c>.worktrees/lucidview</c> so the just-added
    /// worktree can build the App: its csproj references <c>..\..\..\lucidview</c>, which from a worktree
    /// resolves to <c>.worktrees/lucidview</c>. Idempotent (a HEAD stamp makes an unchanged source a no-op),
    /// so it's safe to call on every worktree spawn. A failure is traced but does not fail the spawn — the
    /// agent still gets its isolated tree; if it then can't build it will surface that itself.
    /// </summary>
    private static async Task EnsureLucidViewProvisionedAsync(string repoRoot)
    {
        var result = await Styloagent.Git.LucidViewProvisioner.EnsureAsync(repoRoot);
        if (!result.Ok)
            System.Diagnostics.Trace.WriteLine(
                $"[Styloagent] lucidview provisioning {result.Status}: {result.Detail} — a worktree agent may fail to build");
    }

    private static void EnsureWorktreesIgnored(string repoRoot)
    {
        try
        {
            var exclude = Path.Combine(repoRoot, ".git", "info", "exclude");
            if (!File.Exists(exclude)) return;
            var lines = File.ReadAllLines(exclude);
            if (lines.Any(l => l.Trim() == ".worktrees/")) return;
            File.AppendAllText(exclude, Environment.NewLine + ".worktrees/" + Environment.NewLine);
        }
        catch { /* ignoring is best-effort */ }
    }

    /// <summary>
    /// Disposes managed resources, including the <see cref="BusViewModel"/> (which
    /// stops the FileSystemWatcher and cleans up the debounce timer), and the MCP server.
    /// </summary>
    public void Dispose()
    {
        _routerHost?.Dispose();
        _routerHost = null;

        _gitWatcher?.Dispose();
        _gitWatcher = null;

        _diagramDebounceTimer?.Stop();
        _diagramDebounceTimer = null;

        _idleTimer?.Stop();
        _idleTimer = null;

        if (_dockFactory is not null)
            _dockFactory.ActiveDockableChanged -= OnActiveDockableChanged;

        ProposedTeam?.Dispose();
        _busViewModel?.Dispose();
        _searchIndex.Dispose();

        // Tear down every federated repo instance: its bus feed + its own hooks channel (and temp dir).
        foreach (var inst in _repoInstances)
        {
            inst.Bus.Dispose();
            if (inst.Hooks is { } h)
            {
                h.EventReceived -= OnHookEvent;
                string dir = h.HooksDirectory;
                _ = h.DisposeAsync().AsTask().ContinueWith(_ =>
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                    catch { /* temp dir cleanup is best-effort */ }
                });
            }
        }
        _repoInstances.Clear();

        if (_mcpServer is not null)
        {
            // Fire-and-forget: stop and dispose the Kestrel server asynchronously.
            _ = _mcpServer.DisposeAsync().AsTask();
            _mcpServer = null;
        }

        if (_hookChannel is not null)
        {
            _hookChannel.EventReceived -= OnHookEvent;
            string dir = _hookChannel.HooksDirectory;
            // Fire-and-forget: stop polling, then best-effort remove the temp drop-dir.
            _ = _hookChannel.DisposeAsync().AsTask().ContinueWith(_ =>
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* temp dir cleanup is best-effort */ }
            });
            _hookChannel = null;
        }
    }
}
