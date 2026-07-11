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
#pragma warning restore CA1707
}
