using System.Text.Json;

namespace Styloagent.App.Mcp;

/// <summary>Builds runtime-native MCP config args launched agents use to reach our server.</summary>
public static class McpConfig
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static string BuildJson(string prefix, Uri url, string token)
    {
        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["styloagent"] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = url.ToString(),
                    ["headers"] = new Dictionary<string, string>
                    {
                        ["X-Styloagent-Agent"] = prefix,
                        ["Authorization"] = $"Bearer {token}",
                    },
                },
            },
        };
        return JsonSerializer.Serialize(config, IndentedJson);
    }

    public static IReadOnlyList<string> Args(string prefix, Uri url, string token)
        => ["--mcp-config", BuildJson(prefix, url, token)];

    public static IReadOnlyList<string> CodexArgs(string prefix, Uri url, string token) =>
    [
        "--config", "mcp_servers.styloagent.enabled=true",
        "--config", $"mcp_servers.styloagent.url={TomlString(url.ToString())}",
        "--config", "mcp_servers.styloagent.default_tools_approval_mode=\"auto\"",
        "--config", $"mcp_servers.styloagent.http_headers={{\"X-Styloagent-Agent\"={TomlString(prefix)},\"Authorization\"={TomlString($"Bearer {token}")}}}",
    ];

    private static string TomlString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }
}
