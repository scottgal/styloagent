using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>The MCP tools agents call to acquire and release shared resources via the router.</summary>
[McpServerToolType]
public sealed class RouterTools
{
    private readonly IHttpContextAccessor _http;
    private readonly IRouterController _controller;
    private readonly McpAuth _auth;

    public RouterTools(IHttpContextAccessor http, IRouterController controller, McpAuth auth)
        => (_http, _controller, _auth) = (http, controller, auth);

#pragma warning disable CA1707 // Identifiers should not contain underscores — tool names are MCP contract and must match the wire protocol
    [McpServerTool, Description("Claim exclusive access to a shared resource (slot or account). env is the environment name (e.g. prod/staging); resource is the slot or account name; purpose is a short description of what you need it for.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> claim(string env, string resource, string purpose)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        return await _controller.ClaimAsync(caller, env, resource, purpose).ConfigureAwait(false);
    }

    [McpServerTool, Description("Send a heartbeat for an active resource grant to prevent it expiring due to lease TTL. Call roughly every 30 s while you still hold the resource.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> heartbeat(string env, string resource)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        return await _controller.HeartbeatAsync(caller, env, resource).ConfigureAwait(false);
    }

    [McpServerTool, Description("Release your hold or pending claim on a resource. Always call this when you are done — do not let the lease expire passively.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> release(string env, string resource)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        return await _controller.ReleaseAsync(caller, env, resource).ConfigureAwait(false);
    }

    [McpServerTool, Description("Log an authentication attempt against an account resource. ok=true for success, ok=false for failure. Used by the router to track lockout state.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> log_attempt(string env, string account, bool ok)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        return await _controller.LogAttemptAsync(caller, env, account, ok).ConfigureAwait(false);
    }

    [McpServerTool, Description("Return the current router state: holders, queues, and cooldowns. Pass env to filter to one environment, or leave blank for all environments.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> router_status(string env)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        return await _controller.StatusAsync(string.IsNullOrWhiteSpace(env) ? null : env).ConfigureAwait(false);
    }

    [McpServerTool, Description("Register an environment for routed SSH, Playwright, deployment and shared-resource access. Only the environment control owner (overview- by default) may register one.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> register_environment(string id, string display_name, string classification)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller
            .RegisterEnvironmentAsync(caller, id, display_name, classification).ConfigureAwait(false);
    }

    [McpServerTool, Description("Configure an environment's allow-listed Playwright origin, approved opaque credential reference, and read/write concurrency. Only the environment control owner may change this hard policy. Never pass a credential value.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> configure_browser_environment(string environment, string web_origin,
        string browser_credential_ref, int read_capacity, int write_capacity)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller.ConfigureBrowserEnvironmentAsync(
            caller, environment, web_origin,
            string.IsNullOrWhiteSpace(browser_credential_ref) ? null : browser_credential_ref,
            read_capacity, write_capacity).ConfigureAwait(false);
    }

    [McpServerTool, Description("Immediately assign an environment to one agent. Only the environment control owner (overview- by default) may do this.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> assign_environment(string environment, string owner, string reason)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller
            .AssignEnvironmentAsync(caller, environment, owner, reason).ConfigureAwait(false);
    }

    [McpServerTool, Description("Offer ownership of an environment to another agent. The recipient must call accept_environment before authority changes.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> offer_environment(string environment, string owner, string reason)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller
            .OfferEnvironmentAsync(caller, environment, owner, reason).ConfigureAwait(false);
    }

    [McpServerTool, Description("Accept an environment ownership offer addressed to you.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> accept_environment(string environment)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller
            .AcceptEnvironmentAsync(caller, environment).ConfigureAwait(false);
    }

    [McpServerTool, Description("Return an environment you own to its configured fallback owner.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> return_environment(string environment, string reason)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller
            .ReturnEnvironmentAsync(caller, environment, reason).ConfigureAwait(false);
    }

    [McpServerTool, Description("Revoke delegated environment ownership. Only the control owner may revoke. force=true also requires active brokered access to be terminated.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> revoke_environment(string environment, string reason, bool force)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller
            .RevokeEnvironmentAsync(caller, environment, reason, force).ConfigureAwait(false);
    }

    [McpServerTool, Description("Show configured environments, effective owners, pending handoffs and classifications. Pass an empty environment for all.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> environment_status(string environment)
    {
        var caller = AuthorizedCaller();
        return caller is null ? "unauthorized" : await _controller
            .EnvironmentStatusAsync(string.IsNullOrWhiteSpace(environment) ? null : environment).ConfigureAwait(false);
    }

    private string? AuthorizedCaller()
    {
        var ctx = _http.HttpContext;
        return ctx is not null && _auth.TokenOk(ctx) ? McpAuth.CallerPrefix(ctx) : null;
    }
#pragma warning restore CA1707
}
