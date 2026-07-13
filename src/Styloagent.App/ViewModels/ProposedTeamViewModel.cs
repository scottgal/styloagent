using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.App.Config;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;

namespace Styloagent.App.ViewModels;

/// <summary>One proposed subsystem card in the roster's PROPOSED section.</summary>
public sealed partial class ProposedAgentItem : ObservableObject
{
    public ProposedAgent Agent { get; init; } = null!;
    public string Prefix { get; init; } = "";
    public string Responsibility { get; init; } = "";
    public string ColorHex { get; init; } = "#888888";

    /// <summary>Set when a Spawn is rejected by the governor; shown in red on the card.</summary>
    [ObservableProperty]
    private string? _rejectionMessage;
}

/// <summary>
/// Watches the overview's <c>proposed-agents.yaml</c> and exposes the proposals as cards. Spawning a
/// card hands the <see cref="ProposedAgent"/> to the injected callback (the shell turns it into a
/// live roster agent).
/// </summary>
public sealed partial class ProposedTeamViewModel : ObservableObject, IDisposable
{
    private readonly string _path;
    private readonly string? _teamPath;
    private readonly Func<ProposedAgent, SpawnOutcome> _spawn;
    private FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private volatile bool _disposed;

    [ObservableProperty]
    private ObservableCollection<ProposedAgentItem> _proposals = new();

    /// <param name="teamPath">
    /// The repo's committed <c>team.yaml</c> (the portable specialist team that travels with the repo),
    /// or null. Its agents are offered first, ahead of the overview's live <c>proposed-agents.yaml</c>.
    /// </param>
    public ProposedTeamViewModel(string proposedAgentsPath, string? teamPath, Func<ProposedAgent, SpawnOutcome> spawn)
    {
        _path = proposedAgentsPath;
        _teamPath = teamPath;
        _spawn = spawn;
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        Refresh();
        StartWatcher();
    }

    public void Refresh()
    {
        if (_disposed) return;

        // The committed team (picked up from the repo) first, then the overview's live proposals —
        // deduped by prefix so a proposal doesn't double a committed agent.
        var committed = string.IsNullOrEmpty(_teamPath)
            ? (IReadOnlyList<ProposedAgent>)Array.Empty<ProposedAgent>()
            : ProposedAgentsReader.Read(_teamPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agents = committed.Concat(ProposedAgentsReader.Read(_path))
            .Where(a => seen.Add(a.Prefix))
            .ToList();

        var items = agents.Select(a => new ProposedAgentItem
        {
            Agent = a,
            Prefix = a.Prefix,
            Responsibility = a.Responsibility,
            ColorHex = PresentationStore.DefaultColorFor(a.Prefix),
        }).ToList();

        void Update()
        {
            Proposals.Clear();
            foreach (var it in items) Proposals.Add(it);
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) Update();
            else Dispatcher.UIThread.Post(Update);
        }
        catch
        {
            // No UI thread (headless test context): update directly.
            Update();
        }
    }

    [RelayCommand]
    private void Spawn(ProposedAgent agent)
    {
        var outcome = _spawn(agent);
        var item = Proposals.FirstOrDefault(p => ReferenceEquals(p.Agent, agent));
        if (item is null) return;
        if (outcome.Spawned) Proposals.Remove(item);
        else item.RejectionMessage = outcome.Message;
    }

    [RelayCommand]
    private void SpawnAll()
    {
        foreach (var item in Proposals.ToList())
            Spawn(item.Agent);
    }

    private void StartWatcher()
    {
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path)) { EnableRaisingEvents = true };
            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
        }
        catch { /* degrade gracefully */ }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_disposed) _debounce.Change(200, Timeout.Infinite);
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        _debounce.Dispose();
        _watcher?.Dispose();
    }
}
