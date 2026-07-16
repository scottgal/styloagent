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
    [McpServerTool, Description("Launch a child agent under you. prefix is a short lowercase tag ending in '-'. Set worktree=true when this agent's work overlaps files another agent owns, so it runs isolated on its own git worktree/branch; otherwise false to share the repo. Keep launchPrompt SHORT (identity + 'read your mission doc'); pass the full brief as missionDoc — Styloagent writes it to .styloagent/missions/<prefix>.md in the new agent's tree (committed on its branch when worktree=true, so an isolated agent can read it from its own checkout) and tells the agent to read it. Leave missionDoc empty to inject launchPrompt alone.")]
    public async Task<string> spawn_agent(string prefix, string responsibility, string dir, string launchPrompt, bool worktree, string missionDoc = "")
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var parent = McpAuth.CallerPrefix(ctx);
        if (parent is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SpawnAsync(
            new SpawnRequest(parent, prefix, responsibility,
                string.IsNullOrWhiteSpace(dir) ? "." : dir, launchPrompt, worktree, missionDoc ?? string.Empty));

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

    [McpServerTool, Description("Rich live status of the whole fleet: each agent's prefix, responsibility, state (working | idle | needs-you | exited), current activity, seconds since its last output, context usage (e.g. \"83k · 22%\") and whether it has a git worktree — plus working/waiting counts and the paused flag. Use this to see what everyone is doing and who is stalled or blocked.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string fleet_status()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.FleetStatus(), Json);
    }

    [McpServerTool, Description("The most recent operations across the fleet, newest first: time, agent, and what it did (tool use with the file touched, messages, lifecycle). Pass limit (default 30, max 200). Use it to catch up on what happened without watching live.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string read_timeline(int limit)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.ReadTimeline(limit), Json);
    }

    [McpServerTool, Description("Search the project's documents (Lucene, as-you-type prefix, title-boosted) and get the top matches — title + path — so you can read only the relevant docs instead of scanning files. Great for finding design/lifecycle docs, the protocol, plans. Pass a query and optional limit (default 8, max 30). Saves tokens vs. reading whole files.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string search_docs(string query, int limit)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.SearchDocs(query, limit), Json);
    }

    [McpServerTool, Description("List the repos in the open workspace: each repo's name, path, index, overview prefix (e.g. 'overview-' for the primary, 'lucidresume-'), identity colour, and whether it's the primary. A single repo returns one entry. Use this to see which repos you're coordinating across and how to address each repo's overview.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string list_repos()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.ListRepos(), Json);
    }

    [McpServerTool, Description("Lint the fleet's authority graph — the C4 mutation-authority ownership tree. Returns any violations of the invariants that keep the org chart coherent as overviews split: exactly one root, one owner per agent, acyclic, and no overseer (an agent with children) holds a worktree. Empty result means a coherent authority tree. Run after spawning/retiring to catch an incoherent org chart early.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string lint_authority()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.LintAuthority(), Json);
    }

    [McpServerTool, Description("Read what an agent last said — the text of its most recent assistant turn, from its transcript. Use to see what a specialist actually produced/reasoned, not just its state.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> read_agent(string prefix)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return await _controller.ReadAgentAsync(prefix);
    }

    [McpServerTool, Description("Who last touched a file (by path or file name), when, and how (read/edited). Check this BEFORE you access or edit a file another agent may own, so you can coordinate instead of colliding — context beyond worktree boundaries.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string who_touched(string path)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return _controller.WhoTouched(path);
    }

    [McpServerTool, Description("The files most recently touched across the fleet — each with the agent, the operation and how long ago. Pass limit (default 20, max 200). A quick map of where everyone is working.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string recent_files(int limit)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.RecentFiles(limit), Json);
    }

    [McpServerTool, Description("Suspend an agent by prefix: it checkpoints its context and frees its terminal. Use to park an idle specialist and reclaim resources — rehydrate it when you need it again. Returns 'rejected' if it can't (e.g. no checkpoint target).")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> dehydrate_agent(string prefix)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return await _controller.DehydrateAgentAsync(prefix);
    }

    [McpServerTool, Description("Resume a previously dehydrated agent by prefix — it reloads its saved context and comes back live.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> rehydrate_agent(string prefix)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return await _controller.RehydrateAgentAsync(prefix);
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
