using System.Net.Http.Json;
using System.Text.Json;
using Styloagent.App.Mcp;
using Styloagent.Core.Git;
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public class StyloagentMcpServerTests
{
    private sealed class FakeController : IFleetController
    {
        public Task<SpawnOutcome> SpawnAsync(SpawnRequest req) => Task.FromResult(SpawnOutcome.Ok(req.Prefix));
        public FleetSnapshot Snapshot() => new(Array.Empty<FleetMember>(), 12, 3, false);
        public Task<IssueOutcome> ReportIssueAsync(IssueRequest req) => Task.FromResult(IssueOutcome.Ok("issue"));
        public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix) => Task.FromResult(new WrapUpOutcome(WrapUpStatus.Merged, "merged", null));
        public Task<MessageOutcome> SendMessageAsync(MessageRequest req) => Task.FromResult(MessageOutcome.Ok("/ch/inbox/x.md"));
        public Task<string> CaptureScreenshotAsync(string? target) => Task.FromResult("/shots/x.png");
        public FleetStatusReport FleetStatus() => new(Array.Empty<AgentStatus>(), 0, 0, false);
        public IReadOnlyList<TimelineOp> ReadTimeline(int limit) => Array.Empty<TimelineOp>();
        public Task<string> DehydrateAgentAsync(string prefix) => Task.FromResult($"dehydrated {prefix}");
        public Task<string> RehydrateAgentAsync(string prefix) => Task.FromResult($"rehydrated {prefix}");
        public Task<string> ReadAgentAsync(string prefix) => Task.FromResult("done");
        public string WhoTouched(string path) => "none";
        public IReadOnlyList<string> RecentFiles(int limit) => Array.Empty<string>();
        public IReadOnlyList<Styloagent.Core.Docs.DocSearchHit> SearchDocs(string query, int limit) =>
            Array.Empty<Styloagent.Core.Docs.DocSearchHit>();
    }

    private sealed class FakeRouter : IRouterController
    {
        public Task<string> ClaimAsync(string caller, string env, string resource, string purpose) => Task.FromResult("granted");
        public Task<string> HeartbeatAsync(string caller, string env, string resource) => Task.FromResult("ok");
        public Task<string> ReleaseAsync(string caller, string env, string resource) => Task.FromResult("released");
        public Task<string> LogAttemptAsync(string caller, string env, string account, bool ok) => Task.FromResult("logged");
        public Task<string> StatusAsync(string? env) => Task.FromResult("no resources");
    }

    [Fact]
    public void McpConfig_json_names_the_server_url_prefix_and_token()
    {
        var json = McpConfig.BuildJson("foss-", new Uri("http://127.0.0.1:5000/mcp"), "tok");
        Assert.Contains("\"type\": \"http\"", json);
        Assert.Contains("127.0.0.1:5000/mcp", json);
        Assert.Contains("foss-", json);
        Assert.Contains("Bearer tok", json);
        var args = McpConfig.Args("foss-", new Uri("http://127.0.0.1:5000/mcp"), "tok");
        Assert.Equal("--mcp-config", args[0]);
    }

    [Fact]
    public async Task Server_starts_on_loopback_and_lists_the_two_tools()
    {
        await using var server = await StyloagentMcpServer.StartAsync(new FakeController(), new FakeRouter());
        Assert.True(server.IsRunning);
        Assert.Equal("127.0.0.1", server.BaseUrl.Host);

        // MCP tools/list over the streamable-HTTP endpoint.
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list",
                @params = new { }
            })
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {server.Token}");
        req.Headers.TryAddWithoutValidation(McpAuth.AgentHeader, "overview-");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        Assert.True(resp.IsSuccessStatusCode, body);
        Assert.Contains("spawn_agent", body);
        Assert.Contains("list_fleet", body);
    }
}
