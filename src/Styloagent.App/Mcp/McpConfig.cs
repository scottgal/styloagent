using System.Text.Json;
using Styloagent.Core.Sessions;

namespace Styloagent.App.Mcp;

/// <summary>Builds runtime-native MCP config args launched agents use to reach our server.</summary>
public static class McpConfig
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    public static readonly string[] ChromeDevToolsArgs =
    ["-y", "chrome-devtools-mcp@latest", "--isolated", "--headless=true", "--no-usage-statistics",
     "--no-performance-crux", "--redact-network-headers", "--screenshot-format=jpeg", "--screenshot-quality=60"];

    public static string BuildJson(string prefix, Uri url, string token, string? repo = null, bool includeChromeDevTools = true)
    {
        var servers = new Dictionary<string, object>
        {
            ["styloagent"] = new Dictionary<string, object>
            {
                ["type"] = "http", ["url"] = url.ToString(),
                ["headers"] = new Dictionary<string, string> { ["X-Styloagent-Agent"] = prefix, ["X-Styloagent-Repo"] = repo ?? string.Empty, ["Authorization"] = $"Bearer {token}" },
            },
        };
        if (includeChromeDevTools)
            servers["chrome-devtools"] = new Dictionary<string, object> { ["command"] = "npx", ["args"] = ChromeDevToolsArgs };
        var config = new
        {
            mcpServers = servers,
        };
        return JsonSerializer.Serialize(config, IndentedJson);
    }

    // --strict-mcp-config: without it every launched agent also starts its own stdio copy of the
    // operator's plugin MCP servers, costing ~8 node processes and ~500MB of RSS per agent.
    public static IReadOnlyList<string> Args(string prefix, Uri url, string token, string? repo = null, bool includeChromeDevTools = true)
        => ["--mcp-config", BuildJson(prefix, url, token, repo, includeChromeDevTools), "--strict-mcp-config"];

    public static IReadOnlyList<string> CodexArgs(string prefix, Uri url, string token, bool includeChromeDevTools = true)
    {
        var args = new List<string>
        {
        "--config", "mcp_servers.styloagent.enabled=true",
        "--config", $"mcp_servers.styloagent.url={AgentRuntimeProfile.TomlString(url.ToString())}",
        "--config", "mcp_servers.styloagent.default_tools_approval_mode=\"auto\"",
        "--config", $"mcp_servers.styloagent.http_headers={{\"X-Styloagent-Agent\"={AgentRuntimeProfile.TomlString(prefix)},\"Authorization\"={AgentRuntimeProfile.TomlString($"Bearer {token}")}}}",
        };
        if (includeChromeDevTools)
            args.AddRange(["--config", "mcp_servers.chrome-devtools.enabled=true", "--config", "mcp_servers.chrome-devtools.command=\"npx\"", "--config", $"mcp_servers.chrome-devtools.args=[{string.Join(',', ChromeDevToolsArgs.Select(AgentRuntimeProfile.TomlString))}"]);
        return args;
    }

    // TomlString → AgentRuntimeProfile.TomlString (single source of truth)
}
