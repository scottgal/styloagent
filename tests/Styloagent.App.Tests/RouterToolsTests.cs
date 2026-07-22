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
        public Task<string> RegisterEnvironmentAsync(string caller, string id, string displayName, string classification) => Task.FromResult("created");
        public Task<string> ConfigureBrowserEnvironmentAsync(string caller, string environment, string webOrigin, string? browserCredentialRef, int readCapacity, int writeCapacity) => Task.FromResult("configured");
        public Task<string> AssignEnvironmentAsync(string caller, string environment, string owner, string reason) => Task.FromResult("assigned");
        public Task<string> OfferEnvironmentAsync(string caller, string environment, string owner, string reason) => Task.FromResult("offered");
        public Task<string> AcceptEnvironmentAsync(string caller, string environment) => Task.FromResult("accepted");
        public Task<string> ReturnEnvironmentAsync(string caller, string environment, string reason) => Task.FromResult("returned");
        public Task<string> RevokeEnvironmentAsync(string caller, string environment, string reason, bool force) => Task.FromResult("revoked");
        public Task<string> EnvironmentStatusAsync(string? environment) => Task.FromResult("staging — owner: deploy-");
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

    [Fact]
    public async Task environment_operations_require_identity_and_authentication()
    {
        var ctrl = new FakeRouter();
        var tools = new RouterTools(Acc("overview-", "Bearer secret"), ctrl, new McpAuth("secret"));
        Assert.Equal("created", await tools.register_environment("staging", "Staging", "non-production"));
        Assert.Equal("configured", await tools.configure_browser_environment(
            "staging", "https://staging.example.test", "", 2, 1));
        Assert.Equal("assigned", await tools.assign_environment("staging", "deploy-", "delegate deployments"));

        var denied = new RouterTools(Acc("overview-", "Bearer WRONG"), ctrl, new McpAuth("secret"));
        Assert.Equal("unauthorized", await denied.environment_status(""));
    }
}
