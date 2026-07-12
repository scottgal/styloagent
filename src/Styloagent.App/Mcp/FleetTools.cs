using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Styloagent.Core.Git;
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
    [McpServerTool, Description("Launch a child agent under you. prefix is a short lowercase tag ending in '-'. Set worktree=true when this agent's work overlaps files another agent owns, so it runs isolated on its own git worktree/branch; otherwise false to share the repo.")]
    public async Task<string> spawn_agent(string prefix, string responsibility, string dir, string launchPrompt, bool worktree)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var parent = McpAuth.CallerPrefix(ctx);
        if (parent is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SpawnAsync(
            new SpawnRequest(parent, prefix, responsibility,
                string.IsNullOrWhiteSpace(dir) ? "." : dir, launchPrompt, worktree));

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

    [McpServerTool, Description("Return the identity colour (hex, e.g. #4CDB6E) an agent with this prefix will have in the roster. Use it as the component's $bgColor in architecture.md so the C4 colours match the fleet exactly.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string agent_color(string prefix)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return Styloagent.App.Config.PresentationStore.DefaultColorFor(prefix);
    }

    [McpServerTool, Description("File an issue you hit into the project's issues list so the human and other agents can triage it. severity is low | medium | high. Use for blockers, defects, and gaps you cannot resolve yourself — not for routine coordination (use the bus for that).")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> report_issue(string title, string detail, string severity)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.ReportIssueAsync(new IssueRequest(caller, title, detail, severity));
        return outcome.Filed ? outcome.Message : $"rejected: {outcome.Message}";
    }

    [McpServerTool, Description("Send a message to another agent over the bus. 'to' is the recipient's prefix (e.g. 'router-'), or 'all-' to broadcast to every live agent. priority is urgent | normal | low | info. The message is written to the channel as a durable trace and delivered to the recipient immediately. Use this for routine coordination between agents.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> send_message(string to, string subject, string body, string priority)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SendMessageAsync(
            new MessageRequest(caller, to, subject, body ?? string.Empty, priority ?? "normal"));
        return outcome.Sent ? outcome.Message : $"rejected: {outcome.Message}";
    }

    [McpServerTool, Description("Capture a screenshot of the cockpit UI to a PNG file and return its path, which you can then read to see the current UI. Requires the human to have enabled UI automation in Settings (off by default); returns 'rejected' otherwise. Pass an empty target for the whole window.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> screenshot(string target)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";

        return await _controller.CaptureScreenshotAsync(string.IsNullOrWhiteSpace(target) ? null : target);
    }

    [McpServerTool, Description("Signal you have finished your work in your worktree. Styloagent will guard-clean, run the project's tests, merge your branch to main and remove the worktree — or, on failure, keep your worktree and file an issue. Only call when your branch is committed and the work is complete.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> wrap_up()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.WrapUpAsync(caller);
        return outcome.Message;
    }
#pragma warning restore CA1707
}
