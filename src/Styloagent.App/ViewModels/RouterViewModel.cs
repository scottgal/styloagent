using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Environments;
using Styloagent.Core.Browser;
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

/// <summary>A display card for one governed environment and its effective delegated authority.</summary>
public sealed record EnvironmentRow(
    string Id,
    string DisplayName,
    string Description,
    string Classification,
    string Status,
    string Owner,
    string PendingOwner,
    string Color,
    string CapacitySummary,
    string BrowserSummary,
    string TargetSummary);

/// <summary>
/// View-model for the Router panel. Reads the file ledger under <c>routerRoot</c> via
/// <see cref="RouterProjection"/> and exposes one <see cref="RouterResourceRow"/> per resource,
/// showing live holders, queue depth, and any active lockout cooldown. Refreshed on each
/// <c>RouterDecision</c> delivered by <see cref="Styloagent.App.Router.RouterHost"/>.
/// </summary>
public sealed partial class RouterViewModel : ObservableObject
{
    private readonly string _routerRoot;
    private readonly string? _environmentsRoot;
    private readonly string? _browserRoot;

    public ObservableCollection<RouterResourceRow> Resources { get; } = new();
    public ObservableCollection<EnvironmentRow> Environments { get; } = new();

    /// <summary>Empty-state visibility for the view.</summary>
    public bool HasResources => Resources.Count > 0;
    public bool HasEnvironments => Environments.Count > 0;
    public bool IsEmpty => !HasResources && !HasEnvironments;
    public string ControlOwner { get; private set; } = "overview-";

    public RouterViewModel(string routerRoot, string? environmentsRoot = null, string? browserRoot = null)
    {
        _routerRoot = routerRoot;
        _environmentsRoot = environmentsRoot;
        _browserRoot = browserRoot;
    }

    [RelayCommand]
    public void Refresh()
    {
        Resources.Clear();
        Environments.Clear();
        if (_environmentsRoot is not null)
        {
            var registry = EnvironmentOwnershipStore.Read(_environmentsRoot);
            var browserJobs = _browserRoot is null
                ? Array.Empty<BrowserJob>()
                : new BrowserJobStore(_browserRoot).ReadAll();
            ControlOwner = registry.ControlOwner;
            foreach (var environment in registry.Environments)
            {
                var d = environment.Definition;
                var capacity = $"browser {d.Capacity.BrowserRead} read/{d.Capacity.BrowserWrite} write · " +
                               $"ssh {d.Capacity.Ssh} · deploy {d.Capacity.Deploy}";
                var targets = new[] { d.Targets.WebOrigin, d.Targets.ApiOrigin, d.Targets.SshHost }
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                var jobs = browserJobs.Where(j => j.EnvironmentId == d.Id).ToList();
                var activeJobs = jobs.Count(j => j.Status is BrowserJobStatus.Approved or BrowserJobStatus.Running);
                var pendingJobs = jobs.Count(j => j.Status == BrowserJobStatus.Pending);
                var browserSummary = activeJobs == 0 && pendingJobs == 0
                    ? ""
                    : $"Playwright: {activeJobs} active · {pendingJobs} pending";
                Environments.Add(new EnvironmentRow(d.Id, d.DisplayName, d.Description, d.Classification,
                    d.Status, environment.Owner,
                    environment.PendingOwner is null ? "" : $"offered to {environment.PendingOwner}",
                    d.Color, capacity, browserSummary, string.Join(" · ", targets)));
            }
        }
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
        OnPropertyChanged(nameof(HasEnvironments));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ControlOwner));
    }
}
