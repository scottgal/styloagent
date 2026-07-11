using Microsoft.AspNetCore.Http;
using Styloagent.App.Mcp;
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public class RouterToolsTests
{
    private sealed class FakeRouter : IRouterController
    {
        public string? LastClaimCaller, LastEnv, LastResource;
        public Task<string> ClaimAsync(string caller, string env, string resource, string purpose)
        { LastClaimCaller = caller; LastEnv = env; LastResource = resource; return Task.FromResult($"claimed {resource}"); }
        public Task<string> HeartbeatAsync(string caller, string env, string resource) => Task.FromResult("ok");
        public Task<string> ReleaseAsync(string caller, string env, string resource) => Task.FromResult("released");
        public Task<string> LogAttemptAsync(string caller, string env, string account, bool ok) => Task.FromResult("logged");
        public Task<string> StatusAsync(string? env) => Task.FromResult("prod/deploy: held by foss-");
    }

    private static IHttpContextAccessor Acc(string? agent, string? auth)
    {
        var ctx = new DefaultHttpContext();
        if (agent is not null) ctx.Request.Headers[McpAuth.AgentHeader] = agent;
        if (auth is not null) ctx.Request.Headers["Authorization"] = auth;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    [Fact]
    public async Task claim_uses_caller_prefix_and_returns_disposition()
    {
        var ctrl = new FakeRouter();
        var tools = new RouterTools(Acc("foss-", "Bearer secret"), ctrl, new McpAuth("secret"));
        var result = await tools.claim("prod", "deploy", "ship it");
        Assert.Equal("foss-", ctrl.LastClaimCaller);
        Assert.Equal("deploy", ctrl.LastResource);
        Assert.Contains("claimed deploy", result);
    }

    [Fact]
    public async Task claim_refuses_a_bad_token()
    {
        var ctrl = new FakeRouter();
        var tools = new RouterTools(Acc("foss-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        var result = await tools.claim("prod", "deploy", "x");
        Assert.Null(ctrl.LastClaimCaller);
        Assert.Contains("unauthorized", result);
    }
}
