using Microsoft.AspNetCore.Http;
using Styloagent.App.Mcp;
using Styloagent.Core.Mcp;
using Xunit;

namespace Styloagent.App.Tests;

public sealed class BrowserToolsTests
{
    private sealed class FakeBrowser : IBrowserController
    {
        public string? Caller { get; private set; }
        public Task<string> RequestAsync(string caller, string environment, string mode, string purpose,
            string relativePath, string? selector, bool fullPage, string? credentialRef)
        { Caller = caller; return Task.FromResult("pending request-1"); }
        public Task<string> ApproveAsync(string caller, string requestId) => Task.FromResult("approved");
        public Task<string> CancelAsync(string caller, string requestId) => Task.FromResult("cancelled");
        public Task<string> StatusAsync(string? requestId, string? environment) => Task.FromResult("pending");
        public Task<string> ArtifactsAsync(string caller, string requestId) => Task.FromResult("/shot.png");
        public Task RevokeEnvironmentAsync(string caller, string environment) => Task.CompletedTask;
    }

    [Fact]
    public async Task Request_passes_authenticated_agent_identity()
    {
        var fake = new FakeBrowser();
        var tools = new BrowserTools(Accessor("test-", "Bearer secret"), fake, new McpAuth("secret"));
        Assert.Equal("pending request-1", await tools.request_browser_run(
            "staging", "observe", "capture", "/", "", false, ""));
        Assert.Equal("test-", fake.Caller);
    }

    [Fact]
    public async Task Request_rejects_bad_authentication()
    {
        var tools = new BrowserTools(Accessor("test-", "Bearer wrong"), new FakeBrowser(), new McpAuth("secret"));
        Assert.Equal("unauthorized", await tools.request_browser_run(
            "staging", "observe", "capture", "/", "", false, ""));
    }

    private static IHttpContextAccessor Accessor(string agent, string auth)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[McpAuth.AgentHeader] = agent;
        context.Request.Headers.Authorization = auth;
        return new HttpContextAccessor { HttpContext = context };
    }
}
