using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.Config;
using Styloagent.App.Dock;
using Styloagent.Core.Abstractions;
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
        string? presentationPath = null,
        CancellationToken ct = default)
    {
        var vm = new MainWindowViewModel();
        vm._launcher = launcher;
        vm._watcher = watcher;

        var seeder = new ChannelManifestSeeder();
        var entries = await seeder.SeedAsync(channelRoot, new Dictionary<string, string>());
        vm._seededEntries = entries;

        if (entries.Count == 0)
        {
            vm._busViewModel = new BusViewModel(channelRoot, Array.Empty<string>());
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

        var session = new AgentSession(first, launcher, watcher);

        vm.Pane = new AgentPaneViewModel(
            session,
            first,
            presentation.DisplayName,
            presentation.BorderColorHex);

        var knownPrefixes = entries.Select(e => e.Prefix).ToList();
        vm._busViewModel = new BusViewModel(channelRoot, knownPrefixes);

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
        var session = new AgentSession(entry, _launcher, _watcher);
        var paneVm = new AgentPaneViewModel(
            session,
            entry,
            presentation.DisplayName,
            presentation.BorderColorHex);

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
    }
}
