using Microsoft.AspNetCore.Http;
using Styloagent.App.Mcp;
using Styloagent.Core.Git;
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetToolsTests
{
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
        public FleetSnapshot Snapshot() => new(
            new[] { new FleetMember("overview-", "the top", null, 0, "running") }, 12, 3, false);
        public Task<IssueOutcome> ReportIssueAsync(IssueRequest req) { LastIssue = req; return Task.FromResult(NextIssue); }
        public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix) { LastWrapUp = callerPrefix; return Task.FromResult(NextWrapUp); }
        public Task<MessageOutcome> SendMessageAsync(MessageRequest req) { LastMessage = req; return Task.FromResult(NextMessage); }
        public string? LastShotTarget = "unset";
        public Task<string> CaptureScreenshotAsync(string? target) { LastShotTarget = target; return Task.FromResult("/shots/x.png"); }
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
}
