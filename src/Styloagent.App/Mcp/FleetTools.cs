using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Styloagent.Core.Attention;
using Styloagent.Core.Channel;
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
    private readonly PendingInbox _pending;
    private readonly OperatorQuestionHub _operatorQuestions;
    private readonly DocumentOpenHub _documentOpen;

    // pending / operatorQuestions / documentOpen are optional so unit tests can construct FleetTools without
    // wiring a store; in the running server all three are the singletons registered in StyloagentMcpServer
    // (pending rooted at the delivery hooksDir; operatorQuestions + documentOpen constructed by the cockpit VM
    // so its UI and these verbs share one instance). The fallbacks keep an unwired server degrading gracefully.
    public FleetTools(IHttpContextAccessor http, IFleetController controller, McpAuth auth,
        PendingInbox? pending = null, OperatorQuestionHub? operatorQuestions = null, DocumentOpenHub? documentOpen = null)
        => (_http, _controller, _auth, _pending, _operatorQuestions, _documentOpen) =
            (http, controller, auth,
             pending ?? new PendingInbox(System.IO.Path.GetTempPath()),
             operatorQuestions ?? new OperatorQuestionHub(new OperatorQuestionStore(), (_, _, _) => Task.CompletedTask),
             documentOpen ?? new DocumentOpenHub());

#pragma warning disable CA1707 // Identifiers should not contain underscores — tool names are MCP contract and must match the wire protocol
    [McpServerTool, Description("Launch a child agent under you. prefix is a short lowercase tag ending in '-'. Set runtime to 'claude' or 'codex' to make a mixed fleet; model and effort select from agent_capabilities (leave blank for defaults). Set worktree=true when this agent's work overlaps files another agent owns, so it runs isolated on its own git worktree/branch; otherwise false to share the repo. Keep launchPrompt SHORT (identity + 'read your mission doc'); pass the full brief as missionDoc — Styloagent writes it to .styloagent/missions/<prefix>.md in the new agent's tree and tells the agent to read it. Leave missionDoc empty to inject launchPrompt alone.")]
    public async Task<string> spawn_agent(string prefix, string responsibility, string dir, string launchPrompt,
        bool worktree, string missionDoc = "", string runtime = "", string model = "", string effort = "")
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var parent = McpAuth.CallerPrefix(ctx);
        if (parent is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SpawnAsync(
            new SpawnRequest(parent, prefix, responsibility,
                string.IsNullOrWhiteSpace(dir) ? "." : dir, launchPrompt, worktree, missionDoc ?? string.Empty,
                string.IsNullOrWhiteSpace(runtime) ? null : runtime,
                string.IsNullOrWhiteSpace(model) ? null : model,
                string.IsNullOrWhiteSpace(effort) ? null : effort));

        return outcome.Spawned ? outcome.Message : $"rejected: {outcome.Message}";
    }

    [McpServerTool, Description("Rename an agent's cockpit identity and broadcast the stable-prefix to display-name mapping to the fleet. The prefix does not change, so bus routing remains stable.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> rename_agent(string prefix, string name)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return await _controller.RenameAgentAsync(prefix, name);
    }

    [McpServerTool, Description("Return the live model and effort choices for each supported agent runtime. The list is reloaded from .styloagent/agent-capabilities.json for each call, so agents can use the same current choices as spawn_agent without restarting Styloagent.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string agent_capabilities()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.AgentCapabilities(), Json);
    }

    [McpServerTool, Description("Return the overview's live job-type model policy, including runtime, model, effort, and the human-readable reasoning for every choice. Reloaded from .styloagent/model-policy.yaml on each call so the overview can adapt it without restarting.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string agent_model_policy()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        return JsonSerializer.Serialize(_controller.ModelPolicy(), Json);
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

    [McpServerTool, Description("Send a message to another agent over the bus. 'to' is the recipient's prefix (e.g. 'router-'), or 'all-' to broadcast to every live agent. priority is urgent | normal | low | info. 'repo' (optional) addresses an agent in ANOTHER open repo by that repo's name (multi-repo workspaces) — leave it empty for the common case of an agent in your OWN repo. The message is written to the channel as a durable trace and delivered to the recipient immediately. Use this for routine coordination between agents.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> send_message(string to, string subject, string body, string priority, string repo = "")
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.SendMessageAsync(
            new MessageRequest(caller, to, subject, body ?? string.Empty, priority ?? "normal",
                string.IsNullOrWhiteSpace(repo) ? null : repo.Trim()));
        return outcome.Sent ? outcome.Message : $"rejected: {outcome.Message}";
    }

    [McpServerTool, Description("Complete a received bus thread with one immutable report. thread is the subject or thread slug from the received note; body must state the completed action, result, and next step. This writes a matching .reply.md record, marks the thread DONE, and removes it from the live Active/Queued lists into Archive. Do not use send_message to reply: that creates a new queued thread.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> reply_to_thread(string thread, string body)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var outcome = await _controller.ReplyToThreadAsync(caller, thread, body ?? string.Empty);
        return outcome.Sent ? outcome.Message : $"rejected: {outcome.Message}";
    }

    [McpServerTool, Description("Pull any bus messages waiting for you and clear them from your inbox — the MCP-native delivery pull. Your session hooks surface messages to you automatically at your turn boundaries; call this yourself at a natural pause to check early, or when you suspect you missed one. Returns the pending message notes (each points at the durable channel file to read), or '(inbox empty)'. Draining does NOT resolve a thread — acknowledgement is still your reply/archive landing in the channel.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string check_inbox()
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var pending = _pending.DrainFormatted(caller);
        return string.IsNullOrWhiteSpace(pending) ? "(inbox empty)" : pending.TrimEnd('\n');
    }

    [McpServerTool, Description("Raise a STRUCTURED question to the HUMAN operator (not another agent) and wait for their pick. Use this when you are blocked on a decision only the human can make — a fork in the approach, a risky/irreversible action, missing intent. 'question' is what you need decided; 'options' are the concrete choices the operator clicks between (2-4 short labels; pass an empty array for a plain acknowledge). The question appears in the cockpit's operator top bar with one-click option buttons. This does NOT block your turn: it returns immediately — end your turn, and the operator's chosen option is delivered back to you as a normal bus message (surfaced at your next turn boundary, or via check_inbox). Prefer this over send_message for human decisions; use send_message for agent-to-agent coordination.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string ask_operator(string question, string[] options)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        if (string.IsNullOrWhiteSpace(question)) return "rejected: question is required";

        var opts = (options ?? Array.Empty<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .ToArray();

        _operatorQuestions.Post(caller, question, opts, DateTimeOffset.Now);
        return "raised to the operator — end your turn; the answer arrives as a normal bus message when the operator picks an option (check_inbox to pull it).";
    }

    [McpServerTool, Description("Surface a document in the cockpit for the HUMAN operator — \"here's THIS doc\", so the operator is looking at the same thing you just wrote or are discussing. 'path' is the document: an ABSOLUTE path, or a path relative to your repo root (e.g. the .md you just created). 'reason' (optional) is a short WHY shown as the pane title/header (e.g. \"here's the seam report\"). The path is canonicalized and must exist within an open repo (rejected otherwise — nothing outside the project opens). Markdown-focused. This does not block your turn and nothing routes back: it returns an ack and the document opens in the operator's cockpit.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public string open_document(string path, string reason = "")
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";

        var repos = _controller.ListRepos();
        var allowedRoots = repos.Select(r => r.Path).ToList();
        var res = DocumentPathResolver.Resolve(path, SenderRepoRoot(caller, repos), allowedRoots);
        if (!res.Ok) return $"rejected: {res.Error}";

        _documentOpen.Post(caller, res.Path!, reason);
        return $"opening {System.IO.Path.GetFileName(res.Path)} in the cockpit for the operator.";
    }

    /// <summary>
    /// The caller's own repo root, for resolving a repo-relative <c>open_document</c> path: the repo whose fleet
    /// the caller runs in, else the workspace's primary repo, else the first open repo (null when none is open).
    /// </summary>
    private string? SenderRepoRoot(string caller, IReadOnlyList<RepoInfo> repos)
    {
        if (repos.Count == 0) return null;
        var callerRepo = _controller.FleetStatus().Agents.FirstOrDefault(a => a.Prefix == caller)?.Repo;
        if (!string.IsNullOrEmpty(callerRepo))
        {
            var match = repos.FirstOrDefault(r => r.Name.Equals(callerRepo, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Path;
        }
        return (repos.FirstOrDefault(r => r.Primary) ?? repos[0]).Path;
    }

    [McpServerTool, Description("Rich live status of the whole fleet: each agent's stable prefix, display name, runtime/model/effort, responsibility, state (working | idle | needs-you | exited), current activity, seconds since its last output, remaining context tokens and pressure, and whether it has a git worktree — plus working/waiting counts and the paused flag.")]
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

    [McpServerTool, Description("Retrieve the small set of hand-editable memory Markdown files relevant to this task. Uses LucidRAG-style hybrid RRF (local Ollama embeddings when available, BM25 title/description matching, salience and freshness), always includes pin: true / ⭐ hard rules, and hard-caps returned context. The index is disposable and rebuilt from the memory files. Pass type to scope (feedback, project, reference, user), limit (default 8, max 20), and maxBytes (default project policy, max 32KB). No synthesis is performed.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> recall_memory(string query, string? type = null, int limit = 8, int maxBytes = 0)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        if (McpAuth.CallerPrefix(ctx) is null) return "unauthorized: missing caller identity";
        var result = await _controller.RecallMemoryAsync(query, type, Math.Clamp(limit, 1, 20), Math.Clamp(maxBytes, 0, 32 * 1024));
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool, Description("Build a bounded briefing pack for the current task from selected sources: memory, docs, bus, issues (all by default). Uses LucidRAG-style RRF over lexical relevance, source salience, and freshness. Bus returns active/recent threads only and boosts messages addressed to you; issues returns open only. Every result is a citeable source-sized block. Live fleet, environment and browser state are deliberately excluded: use their deterministic tools. No synthesis is performed.")]
    [SuppressMessage("Style", "CA1707", Justification = "MCP wire-protocol tool name — underscores are required.")]
    public async Task<string> retrieve_context(string query, string[]? sources = null, int limit = 8, int maxBytes = 6144)
    {
        var ctx = _http.HttpContext;
        if (ctx is null || !_auth.TokenOk(ctx)) return "unauthorized";
        var caller = McpAuth.CallerPrefix(ctx);
        if (caller is null) return "unauthorized: missing caller identity";
        var allowed = new[] { "memory", "docs", "bus", "issues" };
        var requested = (sources ?? []).Where(s => allowed.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
        var result = await _controller.RetrieveContextAsync(caller, query, requested, Math.Clamp(limit, 1, 20), Math.Clamp(maxBytes, 1024, 32 * 1024));
        return JsonSerializer.Serialize(result, Json);
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
