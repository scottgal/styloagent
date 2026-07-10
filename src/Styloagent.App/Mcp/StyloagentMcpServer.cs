using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>
/// In-process HTTP MCP server (loopback, ephemeral port) exposing <see cref="FleetTools"/>. Each
/// launched agent gets a --mcp-config pointing here via <see cref="McpConfigArgs"/>.
/// </summary>
public sealed class StyloagentMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public Uri BaseUrl { get; }
    public string Token { get; }
    public bool IsRunning { get; private set; }

    private StyloagentMcpServer(WebApplication app, Uri baseUrl, string token)
        => (_app, BaseUrl, Token) = (app, baseUrl, token);

    public static async Task<StyloagentMcpServer> StartAsync(IFleetController controller)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(controller);
        builder.Services.AddSingleton(new McpAuth(token));
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<FleetTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync().ConfigureAwait(false);

        var addr = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var baseUrl = new Uri(new Uri(addr), "/mcp");
        return new StyloagentMcpServer(app, baseUrl, token) { IsRunning = true };
    }

    public IReadOnlyList<string> McpConfigArgs(string prefix) => McpConfig.Args(prefix, BaseUrl, Token);

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
