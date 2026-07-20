using System.Text.Json;

namespace Styloagent.Core.Mcp;

/// <summary>The model and effort choices available for one agent runtime.</summary>
public sealed record AgentCapability(string Id, string Label, IReadOnlyList<string> Efforts);

/// <summary>A live, repo-configurable capability list shared by the cockpit, MCP, and agents.</summary>
public sealed record AgentRuntimeCapabilities(string Agent, IReadOnlyList<AgentCapability> Models);

public sealed record AgentCapabilities(IReadOnlyList<AgentRuntimeCapabilities> Agents, string SourcePath)
{
    private static readonly JsonSerializerOptions LoadJson = new() { PropertyNameCaseInsensitive = true };

    public static AgentCapabilities Load(string? repoRoot)
    {
        var path = string.IsNullOrWhiteSpace(repoRoot)
            ? string.Empty
            : Path.Combine(repoRoot, ".styloagent", "agent-capabilities.json");
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AgentCapabilitiesFile>(File.ReadAllText(path), LoadJson);
                if (loaded?.Agents is { Count: > 0 })
                    return new(loaded.Agents.Select(a => new AgentRuntimeCapabilities(
                        a.Agent, a.Models.Select(m => new AgentCapability(m.Id, m.Label ?? m.Id,
                            m.Efforts is { } efforts ? efforts : new List<string> { "default" })).ToList())).ToList(), path);
            }
        }
        catch { /* malformed/missing capability files fall back to safe defaults */ }

        return new(Default, path);
    }

    public bool Supports(string agent, string? model, string? effort)
    {
        var runtime = Agents.FirstOrDefault(a => a.Agent.Equals(agent, StringComparison.OrdinalIgnoreCase));
        if (runtime is null) return false;
        var selectedModel = string.IsNullOrWhiteSpace(model) ? "default" : model.Trim();
        var capability = runtime.Models.FirstOrDefault(m => m.Id.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
        return capability is not null && (string.IsNullOrWhiteSpace(effort) ||
            capability.Efforts.Any(e => e.Equals(effort.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly IReadOnlyList<AgentRuntimeCapabilities> Default = new[]
    {
        new AgentRuntimeCapabilities("claude", new[]
        {
            new AgentCapability("default", "CLI default", new[] { "default", "low", "medium", "high", "max" }),
            new AgentCapability("haiku", "Claude Haiku", new[] { "default", "low", "medium", "high" }),
            new AgentCapability("sonnet", "Claude Sonnet", new[] { "default", "low", "medium", "high" }),
            new AgentCapability("opus", "Claude Opus", new[] { "default", "low", "medium", "high", "max" }),
        }),
        new AgentRuntimeCapabilities("codex", new[]
        {
            new AgentCapability("default", "CLI default", new[] { "default", "low", "medium", "high", "xhigh" }),
            new AgentCapability("gpt-5-codex", "GPT-5 Codex", new[] { "default", "low", "medium", "high", "xhigh" }),
            new AgentCapability("gpt-5", "GPT-5", new[] { "default", "low", "medium", "high", "xhigh" }),
        }),
    };

    private sealed class AgentCapabilitiesFile
    {
        public List<AgentRuntimeCapabilitiesFile> Agents { get; set; } = new();
    }

    private sealed class AgentRuntimeCapabilitiesFile
    {
        public string Agent { get; set; } = "";
        public List<AgentCapabilityFile> Models { get; set; } = new();
    }

    private sealed class AgentCapabilityFile
    {
        public string Id { get; set; } = "";
        public string? Label { get; set; }
        public List<string>? Efforts { get; set; }
    }
}
