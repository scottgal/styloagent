using Microsoft.AspNetCore.Http;
using Styloagent.App.Mcp;
using Styloagent.Core.Git;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetToolsTests
{
    private static readonly IReadOnlyList<string> TestEfforts = new[] { "low", "high" };
    private sealed class FakeController : IFleetController
    {
        public SpawnRequest? LastReq;
        public SpawnOutcome Next = SpawnOutcome.Ok("foss-");
        public IssueRequest? LastIssue;
        public IssueOutcome NextIssue = IssueOutcome.Ok("some-issue");
        public string? LastWrapUp;
        public WrapUpOutcome NextWrapUp = new(WrapUpStatus.Merged, "merged foss-", null);
        public MessageRequest? LastMessage;
        public MessageOutcome NextMessage = MessageOutcome.Ok("/ch/inbox/router-hello.md");
        public Task<SpawnOutcome> SpawnAsync(SpawnRequest req) { LastReq = req; return Task.FromResult(Next); }
        public Task<string> RenameAgentAsync(string prefix, string name) => Task.FromResult($"renamed {prefix} to {name}");
        public FleetSnapshot Snapshot() => new(
            new[] { new FleetMember("overview-", "the top", null, 0, "running") }, 12, 3, false);
        public AgentCapabilities AgentCapabilities() => new(
            new[] { new AgentRuntimeCapabilities("codex", new[] {
                new AgentCapability("gpt-test", "Test model", TestEfforts) }) }, "test");
        public Styloagent.Core.Projects.ModelPolicy ModelPolicy() => new(
            new ModelPolicySelection(null, null, null, "default reasoning"),
            new Dictionary<string, ModelPolicySelection> { ["tests"] = new(null, "gpt-test", "high", "careful tests") }, "test");
        public Task<IssueOutcome> ReportIssueAsync(IssueRequest req) { LastIssue = req; return Task.FromResult(NextIssue); }
        public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix) { LastWrapUp = callerPrefix; return Task.FromResult(NextWrapUp); }
        public Task<MessageOutcome> SendMessageAsync(MessageRequest req) { LastMessage = req; return Task.FromResult(NextMessage); }
        public string? LastCompletedThread;
        public Task<MessageOutcome> ReplyToThreadAsync(string callerPrefix, string thread, string body)
        {
            LastCompletedThread = thread;
            return Task.FromResult(MessageOutcome.Ok("/ch/outbox/" + thread + ".reply.md"));
        }
        public string? LastShotTarget = "unset";
        public Task<string> CaptureScreenshotAsync(string? target) { LastShotTarget = target; return Task.FromResult("/shots/x.png"); }
        public string? LastDehydrate;
        public FleetStatusReport FleetStatus() => new(
            new[] { new AgentStatus("foss-", "packages", "working", "editing", 3, "41k · 22%", true) }, 1, 0, false);
        public IReadOnlyList<TimelineOp> ReadTimeline(int limit) =>
            new[] { new TimelineOp("14:00:00", "foss-", "editing · Foo.cs") };
        public Task<string> DehydrateAgentAsync(string prefix) { LastDehydrate = prefix; return Task.FromResult($"dehydrated {prefix}"); }
        public Task<string> RehydrateAgentAsync(string prefix) => Task.FromResult($"rehydrated {prefix}");
        public Task<string> ReadAgentAsync(string prefix) => Task.FromResult($"{prefix} said: done");
        public string WhoTouched(string path) => "foss- last touched it 5s ago (editing)";
        public IReadOnlyList<string> RecentFiles(int limit) => new[] { "/repo/Foo.cs — foss- (editing, 5s ago)" };
        public IReadOnlyList<Styloagent.Core.Docs.DocSearchHit> SearchDocs(string query, int limit) =>
            new[] { new Styloagent.Core.Docs.DocSearchHit("PROTOCOL", "/repo/.styloagent/PROTOCOL.md", Styloagent.Core.Docs.DocSource.Repo, ".styloagent/PROTOCOL.md") };
        public IReadOnlyList<RepoInfo>? ReposOverride;   // open_document tests point this at a real temp root
        public IReadOnlyList<RepoInfo> ListRepos() => ReposOverride ?? new[]
        {
            new RepoInfo("styloagent", "/ws/styloagent", 0, "overview-", "#4C9AFF", true),
            new RepoInfo("lucidRESUME", "/ws/lucidRESUME", 1, "lucidresume-", "#5FD08A", false),
        };
        public IReadOnlyList<Styloagent.Core.Architecture.AuthorityViolation> LintAuthority() =>
            new[] { new Styloagent.Core.Architecture.AuthorityViolation("owner-has-worktree", "foss-", "holds a worktree yet has children") };
    }

    private static IHttpContextAccessor AccessorWith(string? agent, string? auth)
    {
        var ctx = new DefaultHttpContext();
        if (agent is not null) ctx.Request.Headers[McpAuth.AgentHeader] = agent;
        if (auth is not null) ctx.Request.Headers["Authorization"] = auth;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    [Fact]
    public async Task spawn_agent_parents_by_header_and_returns_ok()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.spawn_agent("foss-", "owns FOSS", ".", "You are foss-.", worktree: false);

        Assert.Equal("overview-", ctrl.LastReq!.ParentPrefix);
        Assert.Equal("foss-", ctrl.LastReq.Prefix);
        Assert.Contains("spawned foss-", result);
    }

    [Fact]
    public async Task rename_agent_requires_auth_and_forwards_name()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.rename_agent("agent-1-", "Planner");

        Assert.Equal("renamed agent-1- to Planner", result);
    }

    [Fact]
    public async Task spawn_agent_reports_a_rejection()
    {
        var ctrl = new FakeController { Next = SpawnOutcome.Reject(RejectReason.FleetFull, "fleet full (12/12)") };
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.spawn_agent("foss-", "r", ".", "p", worktree: false);
        Assert.Contains("rejected", result);
        Assert.Contains("fleet full", result);
    }

    [Fact]
    public async Task spawn_agent_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.spawn_agent("foss-", "r", ".", "p", worktree: false);
        Assert.Null(ctrl.LastReq);            // never reached the controller
        Assert.Contains("unauthorized", result);
    }

    [Fact]
    public void list_fleet_serializes_the_snapshot()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var json = tools.list_fleet();
        Assert.Contains("overview-", json);
        Assert.Contains("\"maxFleet\"", json);
    }

    [Fact]
    public async Task report_issue_files_with_the_caller_as_reporter()
    {
        var ctrl = new FakeController { NextIssue = IssueOutcome.Ok("build-broken") };
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.report_issue("Build broken", "npm run build fails", "high");

        Assert.Equal("foss-", ctrl.LastIssue!.Reporter);
        Assert.Equal("Build broken", ctrl.LastIssue.Title);
        Assert.Equal("high", ctrl.LastIssue.Severity);
        Assert.Contains("filed build-broken", result);
    }

    [Fact]
    public async Task report_issue_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.report_issue("t", "d", "low");
        Assert.Null(ctrl.LastIssue);
        Assert.Contains("unauthorized", result);
    }

    [Fact]
    public async Task spawn_agent_passes_the_worktree_flag_through()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        await tools.spawn_agent("foss-", "owns FOSS", ".", "You are foss-.", worktree: true);

        Assert.True(ctrl.LastReq!.Worktree);
    }

    [Fact]
    public async Task spawn_agent_passes_runtime_through_for_mixed_fleets()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        await tools.spawn_agent("codex-", "owns CLI hooks", ".", "You are codex-.", worktree: false,
            runtime: "codex");

        Assert.Equal("codex", ctrl.LastReq!.Runtime);
    }

    [Fact]
    public async Task spawn_agent_passes_model_and_effort_through()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        await tools.spawn_agent("codex-", "owns CLI hooks", ".", "You are codex-.", worktree: false,
            runtime: "codex", model: "gpt-test", effort: "high");

        Assert.Equal("gpt-test", ctrl.LastReq!.Model);
        Assert.Equal("high", ctrl.LastReq.Effort);
    }

    [Fact]
    public void agent_capabilities_serializes_the_live_catalog()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var json = tools.agent_capabilities();
        Assert.Contains("gpt-test", json);
        Assert.Contains("high", json);
    }

    [Fact]
    public void agent_model_policy_serializes_reasoning()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var json = tools.agent_model_policy();
        Assert.Contains("careful tests", json);
        Assert.Contains("gpt-test", json);
    }

    [Fact]
    public async Task send_message_sends_with_the_caller_as_sender()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.send_message("router-", "Need a review", "PR is up", "normal");

        Assert.Equal("foss-", ctrl.LastMessage!.From);
        Assert.Equal("router-", ctrl.LastMessage.To);
        Assert.Equal("Need a review", ctrl.LastMessage.Subject);
        Assert.Contains("sent", result);
    }

    [Fact]
    public async Task send_message_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.send_message("router-", "s", "b", "normal");
        Assert.Null(ctrl.LastMessage);        // never reached the controller
        Assert.Contains("unauthorized", result);
    }

    [Fact]
    public async Task send_message_passes_the_repo_through_for_cross_repo_addressing()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));

        await tools.send_message("overview-", "cross", "hi", "normal", "styloissues");

        Assert.Equal("styloissues", ctrl.LastMessage!.Repo);   // cross-repo target carried to the controller
    }

    [Fact]
    public async Task reply_to_thread_uses_the_completion_path_not_a_new_message()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.reply_to_thread("need-a-review", "Reviewed: approved. Next: merge.");

        Assert.Equal("need-a-review", ctrl.LastCompletedThread);
        Assert.Contains("reply.md", result);
    }

    [Fact]
    public async Task send_message_defaults_repo_to_null_for_intra_repo_back_compat()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));

        await tools.send_message("router-", "s", "b", "normal");   // no repo arg → sender's own repo

        Assert.Null(ctrl.LastMessage!.Repo);
    }

    [Fact]
    public async Task screenshot_returns_the_capture_path()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));
        var result = await tools.screenshot("");
        Assert.Null(ctrl.LastShotTarget);     // empty target normalized to null (whole window)
        Assert.Contains(".png", result);
    }

    [Fact]
    public async Task screenshot_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.screenshot("");
        Assert.Equal("unset", ctrl.LastShotTarget);   // never reached the controller
        Assert.Contains("unauthorized", result);
    }

    [Fact]
    public void fleet_status_serializes_the_rich_report()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var json = tools.fleet_status();
        Assert.Contains("foss-", json);
        Assert.Contains("editing", json);
        Assert.Contains("\"working\"", json);
        Assert.Contains("\"working\":1", json.Replace(" ", ""));
    }

    [Fact]
    public void read_timeline_serializes_recent_ops()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        Assert.Contains("Foo.cs", tools.read_timeline(10));
    }

    [Fact]
    public async Task dehydrate_agent_targets_the_prefix()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));
        var result = await tools.dehydrate_agent("foss-");
        Assert.Equal("foss-", ctrl.LastDehydrate);
        Assert.Contains("dehydrated foss-", result);
    }

    [Fact]
    public async Task dehydrate_agent_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.dehydrate_agent("foss-");
        Assert.Null(ctrl.LastDehydrate);
        Assert.Contains("unauthorized", result);
    }

    [Fact]
    public void who_touched_reports_the_last_toucher()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        Assert.Contains("foss-", tools.who_touched("/repo/Foo.cs"));
    }

    [Fact]
    public async Task read_agent_returns_the_agents_last_output()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        Assert.Contains("done", await tools.read_agent("foss-"));
    }

    [Fact]
    public void recent_files_serializes_the_list()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        Assert.Contains("Foo.cs", tools.recent_files(20));
    }

    [Fact]
    public void search_docs_serializes_hits()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        Assert.Contains("PROTOCOL", tools.search_docs("proto", 8));
    }

    [Fact]
    public void list_repos_serializes_the_workspace_repos()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var json = tools.list_repos();
        Assert.Contains("overview-", json);
        Assert.Contains("lucidresume-", json);
        Assert.Contains("\"primary\":true", json.Replace(" ", ""));
    }

    [Fact]
    public void list_repos_refuses_a_bad_token()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer WRONG"), new FakeController(), new McpAuth("secret"));
        Assert.Contains("unauthorized", tools.list_repos());
    }

    [Fact]
    public void lint_authority_serializes_violations()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var json = tools.lint_authority();
        Assert.Contains("owner-has-worktree", json);
        Assert.Contains("foss-", json);
    }

    [Fact]
    public void lint_authority_refuses_a_bad_token()
    {
        var tools = new FleetTools(AccessorWith("overview-", "Bearer WRONG"), new FakeController(), new McpAuth("secret"));
        Assert.Contains("unauthorized", tools.lint_authority());
    }

    [Fact]
    public void list_fleet_refuses_a_bad_token_or_missing_identity()
    {
        // (a) a wrong bearer token → result contains "unauthorized" and does NOT contain a member prefix
        var toolsWithBadToken = new FleetTools(AccessorWith("overview-", "Bearer WRONG"), new FakeController(), new McpAuth("secret"));
        var resultBadToken = toolsWithBadToken.list_fleet();
        Assert.Contains("unauthorized", resultBadToken);
        Assert.DoesNotContain("overview-", resultBadToken);

        // (b) a valid token but NO X-Styloagent-Agent header → result contains "unauthorized"
        var toolsWithoutIdentity = new FleetTools(AccessorWith(null, "Bearer secret"), new FakeController(), new McpAuth("secret"));
        var resultNoIdentity = toolsWithoutIdentity.list_fleet();
        Assert.Contains("unauthorized", resultNoIdentity);
    }

    [Fact]
    public async Task wrap_up_uses_the_caller_prefix_and_returns_the_message()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.wrap_up();

        Assert.Equal("foss-", ctrl.LastWrapUp);
        Assert.Contains("merged foss-", result);
    }

    [Fact]
    public async Task wrap_up_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("foss-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.wrap_up();
        Assert.Null(ctrl.LastWrapUp);
        Assert.Contains("unauthorized", result);
    }

    // ---- check_inbox (MCP-native delivery pull) -------------------------------

    private static Styloagent.Core.Channel.PendingInbox TempInbox() =>
        new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "styloagent-fleettools-inbox", System.Guid.NewGuid().ToString("N")));

    [Fact]
    public void check_inbox_drains_the_callers_pending_notes_and_then_reports_empty()
    {
        var inbox = TempInbox();
        inbox.Enqueue("foss-", "[bus] normal \"topic\" — read it: /ch/inbox/foss-topic.md", pushing: true);
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), new FakeController(), new McpAuth("secret"), inbox);

        var first = tools.check_inbox();
        Assert.Contains("topic", first);          // the pending note came back to its owner

        var second = tools.check_inbox();
        Assert.Equal("(inbox empty)", second);    // draining cleared it
    }

    [Fact]
    public void check_inbox_only_returns_the_callers_own_messages()
    {
        var inbox = TempInbox();
        inbox.Enqueue("router-", "for router only", pushing: true);
        var tools = new FleetTools(AccessorWith("foss-", "Bearer secret"), new FakeController(), new McpAuth("secret"), inbox);

        Assert.Equal("(inbox empty)", tools.check_inbox());   // foss- sees nothing addressed to router-
    }

    [Fact]
    public void check_inbox_refuses_a_bad_token()
    {
        var inbox = TempInbox();
        inbox.Enqueue("foss-", "secret note", pushing: true);
        var tools = new FleetTools(AccessorWith("foss-", "Bearer WRONG"), new FakeController(), new McpAuth("secret"), inbox);

        Assert.Contains("unauthorized", tools.check_inbox());
        Assert.True(inbox.HasPending("foss-"));   // an unauthorized call must not drain the inbox
    }

    // ---- ask_operator (structured question to the human) ----------------------

    private static readonly string[] ShipOptions = { "Ship it", "Hold" };
    private static readonly string[] OptionsWithBlanks = { "Yes", "  ", "", "No" };
    private static readonly string[] YesNo = { "Yes", "No" };

    private static (FleetTools Tools, Styloagent.Core.Attention.OperatorQuestionHub Hub) ToolsWithHub(string? agent, string auth)
    {
        var hub = new Styloagent.Core.Attention.OperatorQuestionHub(
            new Styloagent.Core.Attention.OperatorQuestionStore(), (_, _, _) => Task.CompletedTask);
        var tools = new FleetTools(AccessorWith(agent, auth), new FakeController(), new McpAuth("secret"), null, hub);
        return (tools, hub);
    }

    [Fact]
    public void ask_operator_posts_the_callers_question_with_its_options()
    {
        var (tools, hub) = ToolsWithHub("foss-", "Bearer secret");

        var result = tools.ask_operator("Merge or rebase?", ShipOptions);

        Assert.Contains("operator", result);
        var pending = Assert.Single(hub.Pending);
        Assert.Equal("foss-", pending.AskingPrefix);            // keyed by the asking agent
        Assert.Equal("Merge or rebase?", pending.Question);
        Assert.Equal(ShipOptions, pending.Options);
    }

    [Fact]
    public void ask_operator_drops_blank_options()
    {
        var (tools, hub) = ToolsWithHub("foss-", "Bearer secret");

        tools.ask_operator("Proceed?", OptionsWithBlanks);

        Assert.Equal(YesNo, hub.Pending[0].Options);
    }

    [Fact]
    public void ask_operator_rejects_an_empty_question()
    {
        var (tools, hub) = ToolsWithHub("foss-", "Bearer secret");

        Assert.Contains("rejected", tools.ask_operator("   ", ShipOptions));
        Assert.Empty(hub.Pending);                              // nothing raised
    }

    [Fact]
    public void ask_operator_refuses_a_bad_token()
    {
        var (tools, hub) = ToolsWithHub("foss-", "Bearer WRONG");

        Assert.Contains("unauthorized", tools.ask_operator("Merge or rebase?", ShipOptions));
        Assert.Empty(hub.Pending);                              // never reached the store
    }

    // ---- open_document (surface a document to the operator) --------------------

    private static string TempRepoWithDoc(out string docPath, string docName = "doc.md")
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "styloagent-opendoc-verb-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        docPath = System.IO.Path.Combine(root, docName);
        System.IO.File.WriteAllText(docPath, "# hi");
        return root;
    }

    private static (FleetTools Tools, Styloagent.Core.Attention.DocumentOpenHub Hub) ToolsWithDocHub(string? agent, string auth, string root)
    {
        var hub = new Styloagent.Core.Attention.DocumentOpenHub();
        var ctrl = new FakeController { ReposOverride = new[] { new RepoInfo("proj", root, 0, "overview-", "#4C9AFF", true) } };
        var tools = new FleetTools(AccessorWith(agent, auth), ctrl, new McpAuth("secret"), null, null, hub);
        return (tools, hub);
    }

    [Fact]
    public void open_document_posts_the_resolved_doc_to_the_hub()
    {
        var root = TempRepoWithDoc(out var doc);
        try
        {
            var (tools, hub) = ToolsWithDocHub("foss-", "Bearer secret", root);
            Styloagent.Core.Attention.DocumentOpenRequest? opened = null;
            hub.Opened += (_, r) => opened = r;

            var result = tools.open_document(doc, "here's the plan");

            Assert.Contains("opening", result);
            Assert.NotNull(opened);
            Assert.Equal("foss-", opened!.AskingPrefix);          // attributed to the asking agent
            Assert.Equal(System.IO.Path.GetFullPath(doc), opened.Path);
            Assert.Equal("here's the plan", opened.Reason);
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void open_document_resolves_a_repo_relative_path_against_the_open_repo()
    {
        var root = TempRepoWithDoc(out _);
        try
        {
            var (tools, hub) = ToolsWithDocHub("foss-", "Bearer secret", root);
            Styloagent.Core.Attention.DocumentOpenRequest? opened = null;
            hub.Opened += (_, r) => opened = r;

            tools.open_document("doc.md", "");                    // relative to the repo root

            Assert.Equal(System.IO.Path.Combine(root, "doc.md"), opened!.Path);
            Assert.Null(opened.Reason);                           // blank reason → null
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void open_document_rejects_a_path_outside_the_open_repo()
    {
        var root = TempRepoWithDoc(out _);
        try
        {
            var (tools, hub) = ToolsWithDocHub("foss-", "Bearer secret", root);
            bool opened = false;
            hub.Opened += (_, _) => opened = true;

            var result = tools.open_document("/etc/hosts", "");

            Assert.Contains("rejected", result);
            Assert.False(opened);                                 // nothing outside the project opens
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void open_document_refuses_a_bad_token()
    {
        var (tools, hub) = ToolsWithDocHub("foss-", "Bearer WRONG", System.IO.Path.GetTempPath());
        bool opened = false;
        hub.Opened += (_, _) => opened = true;

        Assert.Contains("unauthorized", tools.open_document("/x/doc.md", ""));
        Assert.False(opened);                                     // never reached the hub
    }
}
