using System.Text.Json;

namespace Styloagent.App.Mcp;

/// <summary>Builds the --mcp-config a launched <c>claude</c> uses to reach our server.</summary>
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
}
