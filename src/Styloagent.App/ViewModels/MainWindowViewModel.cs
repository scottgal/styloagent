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
        var visible = Panes.Where(p => !p.IsHidden).ToList();
        var active = (SelectedPane is { IsHidden: false } sel ? sel : null) ?? visible.FirstOrDefault();
        var layout = _dockFactory.BuildLayout(visible, LayoutMode);
        Layout = layout;
        _dockFactory.InitLayout(layout);
        if (active is not null) _dockFactory.SetActiveDockable(active);
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
        _ = pty.WriteAsync("1\r");
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

    private IGitLog? _gitLog;
    private WorktreeGitWatcher? _gitWatcher;

    private ProjectConfig? _project;
    // The repo the project opened against — captured at InitializeAsync time (before AttachProject sets
    // _project) so the Git panel can fall back to it for agents without their own worktree.
    private string? _repoRoot;
    private RouterHost? _routerHost;

    private IFactory? _factory;
    private StyloagentDockFactory? _dockFactory;
    private BusViewModel? _busViewModel;

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

    // Extra args appended to the first pane's session when launched in overview mode.
    private IReadOnlyList<string> _overviewSystemPromptArgs = Array.Empty<string>();

    // Hook state channel (§4.4): each spawned claude reports lifecycle events into a shared
    // drop-dir; we route them to the owning pane by a per-pane hook id.
    private HookChannel? _hookChannel;
    private readonly Dictionary<string, AgentPaneViewModel> _panesByHookId = new();

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
            _mcpServer = await StyloagentMcpServer.StartAsync(new FleetController(this), new RouterController(this)).ConfigureAwait(false);
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
            ArmDiagramDebounce();

            // Track each pane's lifecycle so dehydrate/rehydrate land on the activity timeline.
            if (e.NewItems is not null)
                foreach (AgentPaneViewModel p in e.NewItems)
                {
                    _paneState[p] = p.State;
                    p.PropertyChanged += OnPaneLifecycleChanged;
                }
        };
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
        CancellationToken ct = default)
    {
        var vm = new MainWindowViewModel();
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
                Transport: AgentTransport.Local);

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
            entries = new[] { overviewEntry }
                .Concat(channelFleet
                    .Where(e => e.Prefix != "overview-")
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
                ? worktrees.Select(w => WorktreeEntry(w, root)).ToList()
                : new[] { WorktreeEntry(new GitWorktree(root, Path.GetFileName(root.TrimEnd('/', '\\')), string.Empty), root) };
        }
        else
        {
            entries = await new ChannelManifestSeeder().SeedAsync(channelRoot, new Dictionary<string, string>());
        }
        vm._seededEntries = entries;

        // The bus feed is routed/coloured by the CHANNEL's own agent prefixes, which are
        // independent of the worktree agents shown as terminals.
        var channelPrefixes = (await new ChannelManifestSeeder()
                .SeedAsync(channelRoot, new Dictionary<string, string>()))
            .Select(e => e.Prefix).ToList();
        vm._busViewModel = new BusViewModel(channelRoot, channelPrefixes)
        {
            OpenDocument = vm.OpenBusMessageDocument,   // double-click a message → its full markdown
            ThreadOpener = vm.OpenBusThreadDocument,     // popout a thread → carousel through it
        };

        // Priority delivery: seed the "already seen" set with the current backlog (so startup does
        // not deliver old messages), then push newly-arrived messages on each bus reload. The policy
        // starts at Default and is refreshed in AttachProject once a project is known.
        vm._channelRoot = channelRoot;
        vm._deliveryService = new MessageDeliveryService(PriorityPolicy.Default, new PtyMessageInjector(vm.ResolvePty));
        vm._deliveryCoordinator = new ChannelDeliveryCoordinator(
            channelRoot, channelPrefixes, vm._deliveryService, vm.SnapshotLiveAgents);
        await vm._deliveryCoordinator.SeedAsync();
        vm._busViewModel.Reloaded += () => _ = vm._deliveryCoordinator.PumpAsync();

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
            vm.HookArgs(firstHookId, first)
              .Concat(vm._overviewSystemPromptArgs)
              .Concat(vm.McpArgsFor(first.Prefix))
              .ToArray());

        vm.Pane = new AgentPaneViewModel(
            session,
            first,
            presentation.DisplayName,
            presentation.BorderColorHex)
        {
            UserInteracted = vm._interaction.RecordInput,
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

        // Assign UserInteracted on first pane after _interaction may have been replaced above.
        vm.Pane.UserInteracted = vm._interaction.RecordInput;

        var dockFactory = new StyloagentDockFactory(vm.Pane, vm._busViewModel);
        vm._dockFactory = dockFactory;
        vm._factory = dockFactory;
        dockFactory.ActiveDockableChanged += vm.OnActiveDockableChanged;
        var layout = dockFactory.CreateLayout();
        vm.Layout = layout;
        if (layout is not null)
            dockFactory.InitLayout(layout);

        // A pane IS a claude terminal: launch the agent immediately so the pane comes
        // up running claude. The view attaches to CurrentPty when it renders.
        _ = vm.Pane.SpawnAsync();

        // Multi-repo: each additional repo brings its own overview onto the SHARED bus (the primary repo
        // above anchors on `overview-`). Added here — same thread, same dock, before the window is realised —
        // so every repo overview is built identically to the primary.
        if (extraOverviews is { Count: > 0 })
            foreach (var ov in extraOverviews)
                vm.AddRepoOverview(ov);

        var docRepoRoot = repoRoot ?? Environment.GetEnvironmentVariable("STYLOAGENT_REPO") ?? Directory.GetCurrentDirectory();
        vm.DocLibrary = new DocLibraryViewModel(docRepoRoot, channelRoot, vm.OpenMarkdownDocument)
        {
            ShowSystemMapCommand = vm.ShowSystemMapCommand,
            ShowBusSequenceCommand = vm.ShowBusSequenceCommand,
            ShowArchitectureCommand = vm.ShowArchitectureCommand,
        };
        vm.BuildSearchIndex(docRepoRoot, channelRoot);
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
    public void AddAgent()
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
            entry = nextEntry;
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
                Transport: AgentTransport.Local);
            presentation = new AgentPresentation(
                Prefix: prefix,
                DisplayName: prefix.TrimEnd('-'),
                BorderColorHex: PresentationStore.DefaultColorFor(prefix));
        }

        entry = WithWorkingDir(entry);
        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher, HookArgs(hookId, entry));
        var owner = OverviewPane();   // the overview owns agents added to its fleet
        var paneVm = new AgentPaneViewModel(
            session,
            entry,
            presentation.DisplayName,
            presentation.BorderColorHex)
        {
            UserInteracted = _interaction.RecordInput,
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

        // Launch claude in the new pane immediately.
        _ = paneVm.SpawnAsync();
    }

    /// <summary>
    /// Opens an additional repo's overview agent as a pane on the shared bus (multi-repo workspaces).
    /// Mirrors <see cref="AddAgent"/> but launches claude in that repo's root with the repo's own system
    /// prompt and the fleet MCP, coloured by the repo's hue. The primary repo's overview is opened by
    /// <see cref="InitializeAsync"/>; this adds every additional repo. Idempotent per prefix.
    /// </summary>
    public void AddRepoOverview(Styloagent.Core.Workspace.RepoOverview overview)
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
            Transport: AgentTransport.Local));

        // The specialist team travels with the repo: append THIS repo's own system prompt.
        var systemPromptArgs = File.Exists(overview.SystemPromptPath)
            ? new[] { "--append-system-prompt", File.ReadAllText(overview.SystemPromptPath) }
            : Array.Empty<string>();

        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher,
            HookArgs(hookId, entry)
              .Concat(systemPromptArgs)
              .Concat(McpArgsFor(entry.Prefix))
              .ToArray());

        var paneVm = new AgentPaneViewModel(session, entry, overview.Prefix.TrimEnd('-'), overview.ColorHex)
        {
            UserInteracted = _interaction.RecordInput,
            Host = this,
        };
        Panes.Add(paneVm);
        _panesByHookId[hookId] = paneVm;
        _dockFactory.AddDockable(documentDock, paneVm);

        // Launch claude in the repo's root immediately (the primary pane stays selected).
        _ = paneVm.SpawnAsync();
    }

    /// <summary>Wires the ProposedTeam VM against a project's proposed-agents.yaml. Idempotent.</summary>
    public void AttachProject(ProjectConfig project)
    {
        _project = project;
        FleetPolicy = FleetPolicyReader.Read(project.FleetPolicyPath);
        if (_deliveryService is not null)
            _deliveryService.Policy = PriorityPolicyReader.Read(project.PriorityPolicyPath);
        OnPropertyChanged(nameof(MaxFleet));
        OnPropertyChanged(nameof(MaxDepth));
        OnPropertyChanged(nameof(FleetHudText));
        ProposedTeam?.Dispose();
        ProposedTeam = new ProposedTeamViewModel(project.ProposedAgentsPath, project.TeamPath, SpawnProposedAsync);
        Issues = new IssuesViewModel(project.IssuesDir);

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
            // Auto-rehydrate a parked direct recipient (not broadcasts) before the message lands.
            var recipient = ChannelMessageWriter.NormalizeRecipient(req.To);
            if (recipient != "all-")
            {
                var pane = Panes.FirstOrDefault(p => p.Prefix == recipient);
                if (pane is { State: SessionState.Dehydrated })
                    await pane.RehydrateAsync();
            }

            var path = ChannelMessageWriter.Write(
                _channelRoot, req.From, req.To, req.Subject, req.Body ?? string.Empty,
                req.Priority ?? "normal", DateTimeOffset.Now);
            // Deliver now rather than waiting on the debounced fs watcher.
            _ = _deliveryCoordinator?.PumpAsync();

            var senderColor = Panes.FirstOrDefault(p => p.Prefix == req.From)?.BorderColorHex ?? "#8888AA";
            Timeline.Add(DateTimeOffset.Now, req.From, $"→ {req.To} · {req.Subject}", senderColor);

            return MessageOutcome.Ok(path);
        }
        catch (Exception ex)
        {
            return MessageOutcome.Fail(ex.Message);
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

    /// <summary>(Re)builds the document search index from the library files (title + content).</summary>
    private void BuildSearchIndex(string? repoRoot, string? channelRoot)
    {
        try
        {
            var entries = Styloagent.Core.Docs.DocLibraryReader.Read(repoRoot, channelRoot);
            _searchIndex.Build(entries.Select(e => (e, SafeReadFile(e.FullPath))));
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
                owner.Prefix, p.Prefix, p.Responsibility, p.Dir, p.LaunchPrompt, p.Worktree));

        // Root / no-owner exception: establish the single root directly.
        string? worktreePath = null, worktreeBranch = null;
        if (p.Worktree)
        {
            var wt = await TryAddWorktreeAsync(p.Prefix);
            if (!wt.Ok) return SpawnOutcome.Reject(RejectReason.InvalidPrefix, $"worktree add failed: {wt.Error}");
            (worktreePath, worktreeBranch) = (wt.Path, wt.Branch);
        }
        var pane = CreatePaneForProposed(p, worktreeOverride: worktreePath, worktreeBranch: worktreeBranch);
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
            worktreeOverride: worktreePath, worktreeBranch: worktreeBranch);
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

    /// <summary>
    /// Records the open workspace's repos (from its overview list) so the <c>list_repos</c> MCP tool and
    /// repo-grouped UI can enumerate them. A single repo becomes a one-entry list. Set once at startup.
    /// </summary>
    public void SetReposFromOverviews(IReadOnlyList<Styloagent.Core.Workspace.RepoOverview> overviews)
        => _repos = overviews.Select(o => new RepoInfo(
                Name: Path.GetFileName(o.RepoRoot.TrimEnd('/', '\\')),
                Path: o.RepoRoot,
                Index: o.RepoIndex,
                Prefix: o.Prefix,
                ColorHex: o.ColorHex,
                Primary: o.IsPrimary))
            .ToList();

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
        string? worktreeBranch = null)
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
            Transport: AgentTransport.Local);

        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher,
            HookArgs(hookId, entry).Concat(McpArgsFor(p.Prefix)).ToArray());
        var paneVm = new AgentPaneViewModel(
            session,
            entry,
            p.Prefix.TrimEnd('-'),
            PresentationStore.DefaultColorFor(p.Prefix))
        {
            ParentPrefix = parentPrefix,
            Depth = depth,
            Responsibility = p.Responsibility,
            UserInteracted = _interaction.RecordInput,
            Host = this,
        };
        paneVm.WorktreePath = worktreeOverride;
        paneVm.WorktreeBranch = worktreeBranch;
        Panes.Add(paneVm);
        SelectedPane = paneVm;
        _panesByHookId[hookId] = paneVm;

        // The pane IS a Dock Document — add it directly (Id/Title/CanFloat set in its ctor).
        _dockFactory.AddDockable(documentDock, paneVm);
        _dockFactory.SetActiveDockable(paneVm);
        _dockFactory.SetFocusedDockable(rootDock, paneVm);

        // In a tiled mode, re-tile so the new pane gets its own tile rather than a hidden tab.
        if (LayoutMode != CockpitLayoutMode.Tabs) RebuildCenterLayout();

        _ = paneVm.SpawnAsync();
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

        // Re-point the live watcher at whichever checkout we're showing (or stop if there's none).
        _gitWatcher?.Watch(gitDir);

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
    {
        if (_hookChannel is null) return Styloagent.Core.Hooks.HookSettings.PermissionArgs(PermissionMode);
        var hydration = Styloagent.Core.Hooks.HydrationText.For(
            entry.Prefix,
            string.IsNullOrWhiteSpace(entry.SavedContextPath) ? null : entry.SavedContextPath,
            _project?.ProtocolPath,
            _channelRoot);
        var file = _hookChannel.WriteHydrationFile(hookId, hydration);
        return _hookChannel.SettingsArgsFor(hookId, file, PermissionMode)
            .Concat(Styloagent.Core.Hooks.HookSettings.PermissionArgs(PermissionMode))
            .ToList();
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
            pane.ApplyHookEvent(e);
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
        return ext is ".md" or ".markdown" ? DocViewerKind.Markdown : DocViewerKind.Source;
    }

    /// <summary>
    /// Opens a file as the dock document that matches its type (see <see cref="ViewerKindForPath"/>). This is
    /// the single open path shared by the top-bar document search and the drag-onto-the-doc-surface drop, so
    /// they behave identically. No-ops for a blank path or before the dock is initialised.
    /// </summary>
    public void OpenDocumentByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        switch (ViewerKindForPath(path))
        {
            case DocViewerKind.Markdown:
                OpenMarkdownDocument(new MarkdownDocumentViewModel(Path.GetFileNameWithoutExtension(path), path));
                break;
            default:
                OpenSourceDocument(path);
                break;
        }
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
