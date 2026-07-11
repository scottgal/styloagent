using System.Text;
using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;
using Styloagent.Core.Router;

namespace Styloagent.App.Mcp;

/// <summary>Bridges the MCP router tools to the file-backed router ledger via the VM's project root.</summary>
public sealed class RouterController : IRouterController
{
    private readonly MainWindowViewModel _vm;

    public RouterController(MainWindowViewModel vm) => _vm = vm;

    public Task<string> ClaimAsync(string caller, string env, string resource, string purpose)
    {
        var root = _vm.RouterRootOrNull;
        if (root is null) return Task.FromResult("no active project");

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
}
