using System.Text;
using Styloagent.App.ViewModels;
using Styloagent.Core.Environments;
using Styloagent.Core.Mcp;
using Styloagent.Core.Router;

namespace Styloagent.App.Mcp;

/// <summary>Bridges the MCP router tools to the file-backed router ledger via the VM's project root.</summary>
public sealed class RouterController : IRouterController
{
    private readonly MainWindowViewModel _vm;
    private readonly IBrowserController? _browser;

    public RouterController(MainWindowViewModel vm, IBrowserController? browser = null) => (_vm, _browser) = (vm, browser);

    public Task<string> ClaimAsync(string caller, string env, string resource, string purpose)
    {
        var root = _vm.RouterRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        var authority = CheckEnvironmentAuthority(caller, env);
        if (authority is not null) return Task.FromResult(authority);

        RouterClient.DropClaim(root, env, resource, caller, purpose, DateTimeOffset.Now);

        // Read the state post-drop so we can tell the caller their position.
        var state = RouterProjection.Read(root);
        var decisions = RouterResolver.Resolve(state, DateTimeOffset.Now);

        // Check if this caller is being granted immediately.
        var granted = decisions.Any(d =>
            d.Action == RouterAction.Grant &&
            d.Env == env &&
            d.Name == resource &&
            d.Prefix == caller);

        if (granted) return Task.FromResult($"granted — {caller} now holds {env}/{resource}");

        // Otherwise report queue position.
        var res = state.Resources.FirstOrDefault(r => r.Env == env && r.Name == resource);
        if (res is not null)
        {
            var queue = res.Claims
                .OrderBy(c => c.Timestamp).ThenBy(c => c.Prefix, StringComparer.Ordinal)
                .ToList();
            var pos = queue.FindIndex(c => c.Prefix == caller);
            if (pos >= 0)
                return Task.FromResult($"queued at position {pos + 1} for {env}/{resource} — poll router_status");
        }

        return Task.FromResult($"claim recorded for {env}/{resource} — poll router_status");
    }

    public Task<string> HeartbeatAsync(string caller, string env, string resource)
    {
        var root = _vm.RouterRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        var ok = RouterClient.Heartbeat(root, env, resource, caller);
        return Task.FromResult(ok ? "ok" : "no active grant");
    }

    public Task<string> ReleaseAsync(string caller, string env, string resource)
    {
        var root = _vm.RouterRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        RouterClient.Release(root, env, resource, caller);
        return Task.FromResult("released");
    }

    public Task<string> LogAttemptAsync(string caller, string env, string account, bool ok)
    {
        var root = _vm.RouterRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        var authority = CheckEnvironmentAuthority(caller, env, allowActiveHolder: true, account);
        if (authority is not null) return Task.FromResult(authority);
        RouterClient.LogAttempt(root, env, account, ok, DateTimeOffset.Now);
        return Task.FromResult("logged");
    }

    public Task<string> StatusAsync(string? env)
    {
        var root = _vm.RouterRootOrNull;
        if (root is null) return Task.FromResult("no active project");

        var state = RouterProjection.Read(root);
        var now = DateTimeOffset.Now;

        var resources = env is null
            ? state.Resources
            : state.Resources.Where(r => r.Env == env).ToList();

        if (resources.Count == 0)
            return Task.FromResult(env is null ? "no resources" : $"no resources in env '{env}'");

        var sb = new StringBuilder();
        foreach (var r in resources)
        {
            sb.Append($"{r.Env}/{r.Name} ({r.Kind})");

            if (r.Grants.Count > 0)
            {
                var holders = string.Join(", ", r.Grants.Select(g => g.Prefix));
                sb.Append($" — held by {holders}");
            }
            else
            {
                sb.Append(" — free");
            }

            if (r.Claims.Count > 0)
            {
                var queued = string.Join(", ", r.Claims
                    .OrderBy(c => c.Timestamp).ThenBy(c => c.Prefix, StringComparer.Ordinal)
                    .Select(c => c.Prefix));
                sb.Append($"; queued: {queued}");
            }

            if (RouterResolver.IsCooling(r, now, out var until))
                sb.Append($"; cooling until {until:HH:mm:ss}");

            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    public Task<string> RegisterEnvironmentAsync(string caller, string id, string displayName, string classification)
    {
        var root = _vm.EnvironmentsRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        var controlOwner = EnvironmentRegistry.ReadControlOwner(root);
        if (caller != controlOwner)
            return Task.FromResult($"denied: {controlOwner} owns the environment control plane");
        return Task.FromResult(EnvironmentRegistry.Create(root, id, displayName, classification, caller).Message);
    }

    public Task<string> ConfigureBrowserEnvironmentAsync(string caller, string environment, string webOrigin,
        string? browserCredentialRef, int readCapacity, int writeCapacity)
    {
        var root = _vm.EnvironmentsRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        var controlOwner = EnvironmentRegistry.ReadControlOwner(root);
        if (caller != controlOwner)
            return Task.FromResult($"denied: {controlOwner} owns environment policy");
        return Task.FromResult(EnvironmentRegistry.ConfigureBrowser(root, environment, webOrigin,
            browserCredentialRef, readCapacity, writeCapacity).Message);
    }

    public Task<string> AssignEnvironmentAsync(string caller, string environment, string owner, string reason)
        => EnvironmentMutation(service => service.Assign(caller, environment, owner, reason, DateTimeOffset.UtcNow));

    public Task<string> OfferEnvironmentAsync(string caller, string environment, string owner, string reason)
        => EnvironmentMutation(service => service.Offer(caller, environment, owner, reason, DateTimeOffset.UtcNow));

    public Task<string> AcceptEnvironmentAsync(string caller, string environment)
        => EnvironmentMutation(service => service.Accept(caller, environment, DateTimeOffset.UtcNow));

    public Task<string> ReturnEnvironmentAsync(string caller, string environment, string reason)
        => EnvironmentMutation(service => service.Return(caller, environment, reason, DateTimeOffset.UtcNow));

    public async Task<string> RevokeEnvironmentAsync(string caller, string environment, string reason, bool force)
    {
        var message = await EnvironmentMutation(service =>
            service.Revoke(caller, environment, reason, force, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        if (force && message.StartsWith("ownership revoked", StringComparison.Ordinal) && _browser is not null)
            await _browser.RevokeEnvironmentAsync(caller, environment).ConfigureAwait(false);
        return message;
    }

    public Task<string> EnvironmentStatusAsync(string? environment)
    {
        var root = _vm.EnvironmentsRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        var registry = EnvironmentOwnershipStore.Read(root);
        var states = string.IsNullOrWhiteSpace(environment)
            ? registry.Environments
            : registry.Environments.Where(e => e.Definition.Id.Equals(environment, StringComparison.OrdinalIgnoreCase)).ToList();
        if (states.Count == 0) return Task.FromResult("no environments configured");
        var lines = states.Select(e =>
            $"{e.Definition.Id} ({e.Definition.DisplayName}) — {e.Definition.Status}; owner: {e.Owner}" +
            (e.PendingOwner is null ? "" : $"; offered to: {e.PendingOwner}") +
            $"; class: {e.Definition.Classification}");
        return Task.FromResult($"control owner: {registry.ControlOwner}\n" + string.Join('\n', lines));
    }

    private Task<string> EnvironmentMutation(Func<EnvironmentOwnershipService, EnvironmentOperationResult> operation)
    {
        var root = _vm.EnvironmentsRootOrNull;
        if (root is null) return Task.FromResult("no active project");
        return Task.FromResult(operation(new EnvironmentOwnershipService(root)).Message);
    }

    /// <summary>
    /// Registered environments are authority-gated. An unregistered router env retains the legacy
    /// behaviour so existing ledgers remain usable while they are migrated into the registry.
    /// </summary>
    private string? CheckEnvironmentAuthority(string caller, string environment,
        bool allowActiveHolder = false, string? resource = null)
    {
        var environmentsRoot = _vm.EnvironmentsRootOrNull;
        if (environmentsRoot is null) return null;
        var registry = EnvironmentOwnershipStore.Read(environmentsRoot);
        var state = registry.Environments.FirstOrDefault(e =>
            e.Definition.Id.Equals(environment, StringComparison.OrdinalIgnoreCase));
        if (state is null || caller == state.Owner || caller == registry.ControlOwner) return null;
        if (allowActiveHolder && resource is not null && _vm.RouterRootOrNull is { } routerRoot)
        {
            var routed = RouterProjection.Read(routerRoot).Resources.FirstOrDefault(r =>
                r.Env.Equals(environment, StringComparison.OrdinalIgnoreCase) &&
                r.Name.Equals(resource, StringComparison.OrdinalIgnoreCase));
            if (routed?.Grants.Any(g => g.Prefix == caller) == true) return null;
        }
        return $"denied: {state.Owner} owns environment '{state.Definition.Id}'; request access from its owner or {registry.ControlOwner}";
    }
}
