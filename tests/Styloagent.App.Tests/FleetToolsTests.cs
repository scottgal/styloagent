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
        public Task<SpawnOutcome> SpawnAsync(SpawnRequest req) { LastReq = req; return Task.FromResult(Next); }
        public FleetSnapshot Snapshot() => new(
            new[] { new FleetMember("overview-", "the top", null, 0, "running") }, 12, 3, false);
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
}
