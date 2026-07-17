using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Styloagent.Core.Channel;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>
/// In-process HTTP MCP server (loopback, ephemeral port) exposing <see cref="FleetTools"/>. Each
/// launched agent gets a --mcp-config pointing here via <see cref="McpConfigArgs"/>.
/// </summary>
public sealed class StyloagentMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private volatile bool _running;

    public Uri BaseUrl { get; }
    public string Token { get; }
    public bool IsRunning => _running;

    private StyloagentMcpServer(WebApplication app, Uri baseUrl, string token)
        => (_app, BaseUrl, Token) = (app, baseUrl, token);

    /// <param name="hooksDir">
    /// The per-run hooks directory whose <c>deliver/</c> subtree backs the MCP-native delivery queue
    /// (design <c>2026-07-13-mcp-native-delivery-design.md</c>). The <c>check_inbox</c> verb drains the
    /// same <see cref="PendingInbox"/> the delivery service writes to, so both must be rooted here. When
    /// null (not yet wired), <c>check_inbox</c> reads a throwaway store and simply reports an empty inbox.
    /// </param>
    public static async Task<StyloagentMcpServer> StartAsync(
        IFleetController controller, IRouterController router, string? hooksDir = null)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IFleetController>(controller);
        builder.Services.AddSingleton<IRouterController>(router);
        builder.Services.AddSingleton(new McpAuth(token));
        builder.Services.AddSingleton(new PendingInbox(
            hooksDir ?? Path.Combine(Path.GetTempPath(), "styloagent-pending", Guid.NewGuid().ToString("N"))));
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<FleetTools>()
            .WithTools<RouterTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync().ConfigureAwait(false);

        var addr = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var baseUrl = new Uri(new Uri(addr), "/mcp");
        var server = new StyloagentMcpServer(app, baseUrl, token);
        server._running = true;
        return server;
    }

    public IReadOnlyList<string> McpConfigArgs(string prefix) => McpConfig.Args(prefix, BaseUrl, Token);

    public async ValueTask DisposeAsync()
    {
        if (!_running) return;
        _running = false;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
