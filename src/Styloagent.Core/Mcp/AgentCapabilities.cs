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

    private static readonly string[] KiloEfforts = { "default", "low", "medium", "high", "max" };

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
        new AgentRuntimeCapabilities("kilo", new[]
        {
            new AgentCapability("default", "DeepSeek V4 Pro (overview default)", KiloEfforts),
            new AgentCapability("deepseek/deepseek-v4-pro", "DeepSeek V4 Pro", KiloEfforts),
            new AgentCapability("deepseek/deepseek-v4-flash", "DeepSeek V4 Flash", KiloEfforts),
        }),
        new AgentRuntimeCapabilities("claude-deepseek", new[]
        {
            new AgentCapability("default", "deepseek-v4-pro", new[] { "default", "low", "medium", "high", "max" }),
            new AgentCapability("deepseek-v4-pro", "DeepSeek V4 Pro", new[] { "default", "low", "medium", "high", "max" }),
            new AgentCapability("deepseek-v4-flash", "DeepSeek V4 Flash", new[] { "default", "low", "medium", "high", "max" }),
        }),
    };

    /// <summary>
    /// Replaces the <c>kilo</c> runtime's model list with a live catalog discovered from the installed
    /// <c>kilo models</c> CLI. Unknown model ids are dropped; the runtime entry itself is always kept so
    /// the kilo agent stays selectable even when discovery is unavailable.
    /// </summary>
    public AgentCapabilities WithKiloModels(IReadOnlyList<AgentCapability> models)
    {
        if (models is null || models.Count == 0) return this;
        var list = Agents.ToList();
        int idx = list.FindIndex(a => a.Agent.Equals("kilo", StringComparison.OrdinalIgnoreCase));
        var entry = new AgentRuntimeCapabilities("kilo", models);
        if (idx >= 0) list[idx] = entry;
        else list.Add(entry);
        return this with { Agents = list };
    }

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
