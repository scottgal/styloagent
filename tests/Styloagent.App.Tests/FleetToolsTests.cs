using Microsoft.AspNetCore.Http;
using Styloagent.App.Mcp;
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
        public Task<SpawnOutcome> SpawnAsync(SpawnRequest req) { LastReq = req; return Task.FromResult(Next); }
        public FleetSnapshot Snapshot() => new(
            new[] { new FleetMember("overview-", "the top", null, 0, "running") }, 12, 3, false);
        public Task<IssueOutcome> ReportIssueAsync(IssueRequest req) { LastIssue = req; return Task.FromResult(NextIssue); }
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

        var result = await tools.spawn_agent("foss-", "owns FOSS", ".", "You are foss-.");

        Assert.Equal("overview-", ctrl.LastReq!.ParentPrefix);
        Assert.Equal("foss-", ctrl.LastReq.Prefix);
        Assert.Contains("spawned foss-", result);
    }

    [Fact]
    public async Task spawn_agent_reports_a_rejection()
    {
        var ctrl = new FakeController { Next = SpawnOutcome.Reject(RejectReason.FleetFull, "fleet full (12/12)") };
        var tools = new FleetTools(AccessorWith("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));

        var result = await tools.spawn_agent("foss-", "r", ".", "p");
        Assert.Contains("rejected", result);
        Assert.Contains("fleet full", result);
    }

    [Fact]
    public async Task spawn_agent_refuses_a_bad_token()
    {
        var ctrl = new FakeController();
        var tools = new FleetTools(AccessorWith("overview-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.spawn_agent("foss-", "r", ".", "p");
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
}
