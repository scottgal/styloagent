using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>Validates the per-run bearer token and extracts the caller prefix from the request.</summary>
public sealed class McpAuth
{
    public const string AgentHeader = "X-Styloagent-Agent";
    private readonly string _token;
    public McpAuth(string token) => _token = token;

    public bool TokenOk(HttpContext ctx)
        => ctx.Request.Headers.Authorization.ToString() == $"Bearer {_token}";

    public static string? CallerPrefix(HttpContext ctx)
    {
        var v = ctx.Request.Headers[AgentHeader].ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}

/// <summary>The MCP tools agents call: spawn_agent + list_fleet.</summary>
[McpServerToolType]
public sealed class FleetTools
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IHttpContextAccessor _http;
    private readonly IFleetController _controller;
    private readonly McpAuth _auth;

    public FleetTools(IHttpContextAccessor http, IFleetController controller, McpAuth auth)
        => (_http, _controller, _auth) = (http, controller, auth);

#pragma warning disable CA1707 // Identifiers should not contain underscores — tool names are MCP contract and must match the wire protocol
    [McpServerTool, Description("Launch a child agent under you. prefix is a short lowercase tag ending in '-'.")]
    public async Task<string> spawn_agent(string prefix, string responsibility, string dir, string launchPrompt)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var parent = McpAuth.CallerPrefix(ctx);
        if (parent is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SpawnAsync(
            new SpawnRequest(parent, prefix, responsibility,
                string.IsNullOrWhiteSpace(dir) ? "." : dir, launchPrompt));

        return outcome.Spawned ? outcome.Message : $"rejected: {outcome.Message}";
    }

    [McpServerTool, Description("Return the current fleet: each agent's prefix, responsibility, parent, depth and state.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string list_fleet()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.Snapshot(), Json);
    }

    [McpServerTool, Description("Show the architectural impact (+ added / - removed / Impact:) of a proposed C4 change. Pass the current and proposed architecture markdown, each containing a ```mermaid C4...``` block; pass an empty 'before' for a brand-new architecture.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string architecture_impact(string before, string after)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return ArchitectureImpact.Between(string.IsNullOrWhiteSpace(before) ? null : before, after);
    }
#pragma warning restore CA1707
}
