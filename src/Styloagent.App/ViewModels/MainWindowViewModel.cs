using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.Config;
using Styloagent.App.Dock;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Git;
using Styloagent.Core.Hooks;
using Styloagent.Core.Model;
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

    private IFactory? _factory;
    private StyloagentDockFactory? _dockFactory;
    private BusViewModel? _busViewModel;

    // Runtime state for AddAgent
    private IReadOnlyList<AgentManifestEntry> _seededEntries = Array.Empty<AgentManifestEntry>();
    private readonly HashSet<string> _openedPrefixes = new();
    private IPtyLauncher? _launcher;
    private IFileWatcher? _watcher;
    private int _genericAgentCounter;

    // Hook state channel (§4.4): each spawned claude reports lifecycle events into a shared
    // drop-dir; we route them to the owning pane by a per-pane hook id.
    private HookChannel? _hookChannel;
    private readonly Dictionary<string, AgentPaneViewModel> _panesByHookId = new();

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
        if (gitReader is not null)
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
        var session = new AgentSession(first, launcher, watcher, vm.HookArgs(firstHookId));

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
        var layout = dockFactory.CreateLayout();
        vm.Layout = layout;
        if (layout is not null)
            dockFactory.InitLayout(layout);

        // A pane IS a claude terminal: launch the agent immediately so the pane comes
        // up running claude. The view attaches to CurrentPty when it renders.
        _ = vm.Pane.SpawnAsync();

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

    /// <summary>Selects a pane so its terminal is shown.</summary>
    [RelayCommand]
    private void SelectPane(AgentPaneViewModel pane) => SelectedPane = pane;

    /// <summary>Keeps each pane's <see cref="AgentPaneViewModel.IsSelected"/> in sync so the roster
    /// outlines only the active agent.</summary>
    partial void OnSelectedPaneChanged(AgentPaneViewModel? oldValue, AgentPaneViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
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
    /// Disposes managed resources, including the <see cref="BusViewModel"/> (which
    /// stops the FileSystemWatcher and cleans up the debounce timer).
    /// </summary>
    public void Dispose()
    {
        _busViewModel?.Dispose();

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
