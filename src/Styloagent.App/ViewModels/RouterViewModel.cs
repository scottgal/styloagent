using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Router;

namespace Styloagent.App.ViewModels;

/// <summary>A single display row in the Router panel — one row per discovered resource.</summary>
public sealed record RouterResourceRow(
    string Env,
    string Name,
    string Kind,
    string Holders,
    int QueueDepth,
    string Cooldown);

/// <summary>
/// View-model for the Router panel. Reads the file ledger under <c>routerRoot</c> via
/// <see cref="RouterProjection"/> and exposes one <see cref="RouterResourceRow"/> per resource,
/// showing live holders, queue depth, and any active lockout cooldown. Refreshed on each
/// <c>RouterDecision</c> delivered by <see cref="Styloagent.App.Router.RouterHost"/>.
/// </summary>
public sealed partial class RouterViewModel : ObservableObject
{
    private readonly string _routerRoot;

    public ObservableCollection<RouterResourceRow> Resources { get; } = new();

    /// <summary>Empty-state visibility for the view.</summary>
    public bool HasResources => Resources.Count > 0;

    public RouterViewModel(string routerRoot)
    {
        _routerRoot = routerRoot;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Resources.Clear();
        var now = DateTimeOffset.UtcNow;
        var state = RouterProjection.Read(_routerRoot);
        foreach (var r in state.Resources)
        {
            // Live grant: now - g.HeartbeatAt < r.Policy.LeaseTtl  (cannot call internal IsExpired)
            var liveHolders = r.Grants
                .Where(g => now - g.HeartbeatAt < r.Policy.LeaseTtl)
                .Select(g => g.Prefix)
                .ToList();
            var heldSet = new HashSet<string>(liveHolders, StringComparer.Ordinal);
            int queueDepth = r.Claims.Count(c => !heldSet.Contains(c.Prefix));
            RouterResolver.IsCooling(r, now, out var until);
            string cooldown = until != default && now < until
                ? $"cooling until {until.ToLocalTime():HH:mm:ss}"
                : string.Empty;
            Resources.Add(new RouterResourceRow(
                Env: r.Env,
                Name: r.Name,
                Kind: r.Kind.ToString(),
                Holders: string.Join(", ", liveHolders),
                QueueDepth: queueDepth,
                Cooldown: cooldown));
        }
        OnPropertyChanged(nameof(HasResources));
    }
}
