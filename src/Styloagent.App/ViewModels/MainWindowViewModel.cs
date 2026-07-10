using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.Config;
using Styloagent.App.Dock;
using Styloagent.App.Mcp;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Git;
using Styloagent.Core.Hooks;
using Styloagent.Core.Mcp;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;
using Styloagent.Core.Seeding;
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

    [ObservableProperty]
    private AgentPaneViewModel? _selectedPane;

    [ObservableProperty]
    private IRootDock? _layout;

    [ObservableProperty]
    private DocLibraryViewModel? _docLibrary;

    [ObservableProperty]
    private ProposedTeamViewModel? _proposedTeam;

    private ProjectConfig? _project;

    private IFactory? _factory;
    private StyloagentDockFactory? _dockFactory;
    private BusViewModel? _busViewModel;

    // Runtime state for AddAgent
    private IReadOnlyList<AgentManifestEntry> _seededEntries = Array.Empty<AgentManifestEntry>();
    private readonly HashSet<string> _openedPrefixes = new();
    private IPtyLauncher? _launcher;
    private IFileWatcher? _watcher;
    private int _genericAgentCounter;

    // Extra args appended to the first pane's session when launched in overview mode.
    private IReadOnlyList<string> _overviewSystemPromptArgs = Array.Empty<string>();

    // Hook state channel (§4.4): each spawned claude reports lifecycle events into a shared
    // drop-dir; we route them to the owning pane by a per-pane hook id.
    private HookChannel? _hookChannel;
    private readonly Dictionary<string, AgentPaneViewModel> _panesByHookId = new();

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
    private bool _fleetPaused;

    /// <summary>Guardrail limits for this session (read from fleet.yaml on project attach).</summary>
    public FleetPolicy FleetPolicy { get; set; } = FleetPolicy.Default;

    /// <summary>Number of currently-open agent panes.</summary>
    public int FleetCount => Panes.Count;

    /// <summary>Toggles the fleet-paused flag, blocking all governor-checked spawns when true.</summary>
    [RelayCommand]
    private void PauseFleet() => FleetPaused = !FleetPaused;

    // private ctor — callers must use InitializeAsync.
    private MainWindowViewModel() { }

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
        CancellationToken ct = default)
    {
        var vm = new MainWindowViewModel();
        vm._launcher = launcher;
        vm._watcher = watcher;

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
            presentation.BorderColorHex);
        vm.Panes.Add(vm.Pane);
        vm.SelectedPane = vm.Pane;
        vm._panesByHookId[firstHookId] = vm.Pane;

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
        vm.DocLibrary = new DocLibraryViewModel(docRepoRoot, channelRoot, vm.OpenMarkdownDocument);

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
            presentation.BorderColorHex);
        Panes.Add(paneVm);
        SelectedPane = paneVm;
        _panesByHookId[hookId] = paneVm;

        var doc = new Document
        {
            Id = $"AgentPane-{presentation.Prefix}",
            Title = paneVm.DisplayName,
            Context = paneVm,
            CanFloat = true,
        };

        _dockFactory.AddDockable(documentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(rootDock, doc);

        // Launch claude in the new pane immediately.
        _ = paneVm.SpawnAsync();
    }

    /// <summary>Wires the ProposedTeam VM against a project's proposed-agents.yaml. Idempotent.</summary>
    public void AttachProject(ProjectConfig project)
    {
        _project = project;
        FleetPolicy = FleetPolicyReader.Read(project.FleetPolicyPath);
        ProposedTeam?.Dispose();
        ProposedTeam = new ProposedTeamViewModel(project.ProposedAgentsPath, SpawnProposed);
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
        var proposed = new ProposedAgent(req.Prefix, req.Responsibility, req.Dir, req.LaunchPrompt);
        var paneVm = CreatePaneForProposed(proposed, parentPrefix: req.ParentPrefix, depth: parentDepth + 1);
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
        int depth = 0)
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

        var entry = new AgentManifestEntry(
            Prefix: p.Prefix,
            Repo: root,
            Worktree: WorkingDirectoryResolver.Resolve(
                string.IsNullOrWhiteSpace(p.Dir) ? null : Path.Combine(root, p.Dir),
                DefaultWorkingDirectory()),
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
        };
        Panes.Add(paneVm);
        SelectedPane = paneVm;
        _panesByHookId[hookId] = paneVm;

        var doc = new Document
        {
            Id = $"AgentPane-{p.Prefix}",
            Title = paneVm.DisplayName,
            Context = paneVm,
            CanFloat = true,
        };

        _dockFactory.AddDockable(documentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(rootDock, doc);

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
    /// outlines only the active agent.</summary>
    partial void OnSelectedPaneChanged(AgentPaneViewModel? oldValue, AgentPaneViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>Brings the dock document whose context is <paramref name="pane"/> to the front.</summary>
    private void ActivateDocumentFor(AgentPaneViewModel pane)
    {
        if (_dockFactory?.DocumentDock is not { } dock) return;
        var doc = dock.VisibleDockables?
            .OfType<Document>()
            .FirstOrDefault(d => ReferenceEquals(d.Context, pane));
        if (doc is not null)
            _dockFactory.SetActiveDockable(doc);
    }

    /// <summary>Reverse sync: activating a dock tab updates the roster selection/highlight.</summary>
    private void OnActiveDockableChanged(object? sender, global::Dock.Model.Core.Events.ActiveDockableChangedEventArgs e)
    {
        if (e.Dockable is Document { Context: AgentPaneViewModel pane } && !ReferenceEquals(SelectedPane, pane))
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
        Dispatcher.UIThread.Post(() =>
        {
            if (_panesByHookId.TryGetValue(e.AgentId, out var pane) && pane is not null)
                pane.ApplyHookEvent(e);
        });
    }

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

        var doc = new Document
        {
            Id = "Doc-" + Guid.NewGuid().ToString("N"),
            Title = docVm.Title,
            Context = docVm,
            CanFloat = true,
        };

        _dockFactory.AddDockable(documentDock, doc);
        _dockFactory.SetActiveDockable(doc);
        _dockFactory.SetFocusedDockable(rootDock, doc);
    }

    /// <summary>
    /// Disposes managed resources, including the <see cref="BusViewModel"/> (which
    /// stops the FileSystemWatcher and cleans up the debounce timer), and the MCP server.
    /// </summary>
    public void Dispose()
    {
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
