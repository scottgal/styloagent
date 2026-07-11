using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.Config;
using Styloagent.App.Dock;
using Styloagent.App.Mcp;
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
    }

    /// <summary>App-wide light/dark toggle — swaps the structural theme tokens (Fluent variant).</summary>
    [ObservableProperty]
    private bool _isLightTheme;

    partial void OnIsLightThemeChanged(bool value)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = value
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
    }

    [ObservableProperty]
    private IRootDock? _layout;

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

    private IGitLog? _gitLog;

    private ProjectConfig? _project;

    private IFactory? _factory;
    private StyloagentDockFactory? _dockFactory;
    private BusViewModel? _busViewModel;

    // Priority message delivery: pushes new channel messages to their recipient agents per the
    // project's PriorityPolicy (ESC-break for interrupt, defer-until-idle for next-prompt, HUD-only
    // otherwise). Built in InitializeAsync; policy refreshed in AttachProject.
    private MessageDeliveryService? _deliveryService;
    private ChannelDeliveryCoordinator? _deliveryCoordinator;

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
            _mcpServer = await StyloagentMcpServer.StartAsync(new FleetController(this)).ConfigureAwait(false);
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
        Panes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FleetCount));
            OnPropertyChanged(nameof(FleetHudText));
            ArmDiagramDebounce();
        };
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
        CancellationToken ct = default)
    {
        var vm = new MainWindowViewModel();
        vm._launcher = launcher;
        vm._watcher = watcher;
        vm._git = gitService;
        vm._gitLog = gitLog;
        if (gitLog is not null)
            vm.GitGraph = new GitGraphViewModel(gitLog);
        if (gitService is IGitDiff gitDiff)
            vm.Changes = new ChangesViewModel(gitService, gitDiff);

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
            entries = new[]
            {
                new AgentManifestEntry(
                    Prefix: "overview-",
                    Repo: overviewRoot,
                    Worktree: overviewRoot,
                    LaunchPromptPath: string.Empty,
                    RestartPromptPath: string.Empty,
                    SavedContextPath: string.Empty,
                    Transport: AgentTransport.Local),
            };
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
        vm._busViewModel = new BusViewModel(channelRoot, channelPrefixes);

        // Priority delivery: seed the "already seen" set with the current backlog (so startup does
        // not deliver old messages), then push newly-arrived messages on each bus reload. The policy
        // starts at Default and is refreshed in AttachProject once a project is known.
        vm._deliveryService = new MessageDeliveryService(PriorityPolicy.Default, new PtyMessageInjector(vm.ResolvePty));
        vm._deliveryCoordinator = new ChannelDeliveryCoordinator(
            channelRoot, channelPrefixes, vm._deliveryService, vm.SnapshotLiveAgents);
        await vm._deliveryCoordinator.SeedAsync();
        vm._busViewModel.Reloaded += () => _ = vm._deliveryCoordinator.PumpAsync();

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

        string firstHookId = vm.ReserveHookId(first.Prefix);
        var session = new AgentSession(first, launcher, watcher,
            vm.HookArgs(firstHookId)
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

        var docRepoRoot = repoRoot ?? Environment.GetEnvironmentVariable("STYLOAGENT_REPO") ?? Directory.GetCurrentDirectory();
        vm.DocLibrary = new DocLibraryViewModel(docRepoRoot, channelRoot, vm.OpenMarkdownDocument)
        {
            ShowSystemMapCommand = vm.ShowSystemMapCommand,
            ShowBusSequenceCommand = vm.ShowBusSequenceCommand,
            ShowArchitectureCommand = vm.ShowArchitectureCommand,
        };

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
                SavedContextPath: string.Empty,
                Transport: AgentTransport.Local);
            presentation = new AgentPresentation(
                Prefix: prefix,
                DisplayName: prefix.TrimEnd('-'),
                BorderColorHex: PresentationStore.DefaultColorFor(prefix));
        }

        entry = WithWorkingDir(entry);
        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher, HookArgs(hookId));
        var paneVm = new AgentPaneViewModel(
            session,
            entry,
            presentation.DisplayName,
            presentation.BorderColorHex)
        {
            UserInteracted = _interaction.RecordInput,
        };
        Panes.Add(paneVm);
        SelectedPane = paneVm;
        _panesByHookId[hookId] = paneVm;

        // The pane IS a Dock Document — add it directly (Id/Title/CanFloat set in its ctor).
        _dockFactory.AddDockable(documentDock, paneVm);
        _dockFactory.SetActiveDockable(paneVm);
        _dockFactory.SetFocusedDockable(rootDock, paneVm);

        // Launch claude in the new pane immediately.
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
        ProposedTeam = new ProposedTeamViewModel(project.ProposedAgentsPath, SpawnProposed);
        Issues = new IssuesViewModel(project.IssuesDir);
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
    /// active project and that the agent was spawned with a worktree. Runs on the UI thread.
    /// </summary>
    public WrapUpOutcome WrapUp(string callerPrefix)
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

        var outcome = svc.WrapUpAsync(req, policy, _project.IssuesDir).GetAwaiter().GetResult();

        Issues?.Refresh();
        if (outcome.Merged)
        {
            pane.WorktreePath = null;
            pane.WorktreeBranch = null;
        }
        return outcome;
    }

    /// <summary>Turns a proposed subsystem into a live roster agent (mirrors AddAgent).</summary>
    public void SpawnProposed(ProposedAgent p)
    {
        CreatePaneForProposed(p);
    }

    /// <summary>
    /// Governor-checked spawn from a parent agent. Builds fleet state, runs the governor,
    /// and on approval creates the pane with parent/depth lineage stamped in.
    /// </summary>
    public SpawnOutcome SpawnChild(SpawnRequest req)
    {
        var state = new FleetState(BuildFleetSnapshot().Members, FleetPolicy.MaxFleet, FleetPolicy.MaxDepth, FleetPaused);
        var decision = FleetGovernor.Check(state, req.ParentPrefix, req.Prefix);
        if (!decision.Allowed) return SpawnOutcome.Reject(decision.Reason!.Value, decision.Message);

        int parentDepth = Panes.First(p => p.Prefix == req.ParentPrefix).Depth;

        string? worktreePath = null, worktreeBranch = null;
        if (req.Worktree && _git is not null && _project is not null)
        {
            var existing = Panes.Where(p => p.WorktreePath is not null).Select(p => p.WorktreePath!);
            var (path, branch) = WorktreeNaming.For(_project.Root, req.Prefix, existing);
            var add = _git.AddWorktreeAsync(_project.Root, path, branch).GetAwaiter().GetResult();
            if (!add.Ok)
                return SpawnOutcome.Reject(RejectReason.InvalidPrefix, $"worktree add failed: {add.Error}");
            EnsureWorktreesIgnored(_project.Root);
            worktreePath = path;
            worktreeBranch = branch;
        }

        var proposed = new ProposedAgent(req.Prefix, req.Responsibility, req.Dir, req.LaunchPrompt);
        var paneVm = CreatePaneForProposed(proposed, parentPrefix: req.ParentPrefix, depth: parentDepth + 1,
            worktreeOverride: worktreePath, worktreeBranch: worktreeBranch);
        if (worktreePath is not null && _git is not null)
            _ = paneVm!.RefreshGitStatusAsync(_git);
        return paneVm is null
            ? SpawnOutcome.Reject(RejectReason.InvalidPrefix, "could not create pane")
            : SpawnOutcome.Ok(req.Prefix);
    }

    /// <summary>Builds a fleet snapshot from the current roster (for list_fleet and SpawnChild).</summary>
    public FleetSnapshot BuildFleetSnapshot()
    {
        var members = Panes.Select(p => new FleetMember(
            p.Prefix, p.Responsibility, p.ParentPrefix, p.Depth,
            p.HookStateText ?? "running")).ToList();
        return new FleetSnapshot(members, FleetPolicy.MaxFleet, FleetPolicy.MaxDepth, FleetPaused);
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
        string root = _project?.Root ?? DefaultWorkingDirectory();
        string launchPromptPath = string.Empty;
        if (_project is not null && !string.IsNullOrWhiteSpace(p.LaunchPrompt))
        {
            Directory.CreateDirectory(_project.LaunchPromptsDir);
            launchPromptPath = Path.Combine(_project.LaunchPromptsDir, SanitizeFileName(p.Prefix) + ".md");
            File.WriteAllText(launchPromptPath, p.LaunchPrompt);
        }

        string resolvedWorktree = worktreeOverride ?? WorkingDirectoryResolver.Resolve(
            string.IsNullOrWhiteSpace(p.Dir) ? null : Path.Combine(root, p.Dir),
            DefaultWorkingDirectory());

        var entry = new AgentManifestEntry(
            Prefix: p.Prefix,
            Repo: root,
            Worktree: resolvedWorktree,
            LaunchPromptPath: launchPromptPath,
            RestartPromptPath: string.Empty,
            SavedContextPath: string.Empty,
            Transport: AgentTransport.Local);

        string hookId = ReserveHookId(entry.Prefix);
        var session = new AgentSession(entry, _launcher, _watcher,
            HookArgs(hookId).Concat(McpArgsFor(p.Prefix)).ToArray());
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

        _ = paneVm.SpawnAsync();
        return paneVm;
    }

    private static string SanitizeFileName(string s)
        => new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    /// <summary>Selects a pane so its terminal document is brought to front in the centre dock.</summary>
    [RelayCommand]
    private void SelectPane(AgentPaneViewModel pane)
    {
        SelectedPane = pane;
        ActivateDocumentFor(pane);
    }

    /// <summary>Keeps each pane's <see cref="AgentPaneViewModel.IsSelected"/> in sync so the roster
    /// outlines only the active agent. Also loads the worktree graph when the new pane has a path.</summary>
    partial void OnSelectedPaneChanged(AgentPaneViewModel? oldValue, AgentPaneViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        if (newValue?.WorktreePath is { } path)
        {
            if (GitGraph is not null) _ = GitGraph.LoadAsync(path);
            if (Changes is not null) _ = Changes.LoadAsync(path);
        }
    }

    /// <summary>Brings the dock document for <paramref name="pane"/> to the front. The pane IS a
    /// Dock Document, so it is the dockable itself.</summary>
    private void ActivateDocumentFor(AgentPaneViewModel pane)
    {
        if (_dockFactory is null) return;
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

    /// <summary>The <c>--settings</c> hook args for a hook id, or none if the channel is unavailable.</summary>
    private IReadOnlyList<string> HookArgs(string hookId)
        => _hookChannel?.SettingsArgsFor(hookId) ?? Array.Empty<string>();

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
            RefreshAttention();
            if (!_interaction.IsBusy(IdleWindow)) AutoRevealHead();

            // When the agent goes idle, flush any NextPrompt messages that were deferred for it.
            if (_deliveryService is not null)
                _ = _deliveryService.OnRecipientStateChangedAsync(pane.Prefix, pane.HookState);
        }
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
    internal void OnIdleTick()
    {
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
        _diagramDebounceTimer?.Stop();
        _diagramDebounceTimer = null;

        _idleTimer?.Stop();
        _idleTimer = null;

        if (_dockFactory is not null)
            _dockFactory.ActiveDockableChanged -= OnActiveDockableChanged;

        ProposedTeam?.Dispose();
        _busViewModel?.Dispose();

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
