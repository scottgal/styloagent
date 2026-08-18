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
                    return new(loaded.Agents
                        .Where(a => !a.Agent.Equals("kilo", StringComparison.OrdinalIgnoreCase))
                        .Select(a => new AgentRuntimeCapabilities(
                        a.Agent, a.Models.Select(m => new AgentCapability(m.Id, m.Label ?? m.Id,
                            m.Efforts is { } efforts ? efforts : new List<string> { "default" })).ToList())).ToList(), path);
            }
        }
        catch { /* malformed/missing capability files fall back to safe defaults */ }

        return new(Default, path);
    }

    public bool Supports(string agent, string? model, string? effort)
    {
        if (agent.Equals("kilo", StringComparison.OrdinalIgnoreCase)) return false;
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
        }),
        new AgentRuntimeCapabilities("claude-deepseek", new[]
        {
            new AgentCapability("default", "deepseek-v4-pro", new[] { "default", "low", "medium", "high", "max" }),
            new AgentCapability("deepseek-v4-pro", "DeepSeek V4 Pro", new[] { "default", "low", "medium", "high", "max" }),
            new AgentCapability("deepseek-v4-flash", "DeepSeek V4 Flash", new[] { "default", "low", "medium", "high", "max" }),
        }),
    };

    /// <summary>
    /// Replaces the <c>codex</c> runtime's model list with the live catalog discovered from the codex CLI's
    /// own <c>models_cache.json</c> (see <see cref="CodexModelDiscovery"/>). An empty discovery leaves
    /// the static list intact, so Codex stays selectable when its CLI catalog is unavailable.
    /// </summary>
    public AgentCapabilities WithCodexModels(IReadOnlyList<AgentCapability> models)
        => WithDiscoveredModels("codex", models);

    /// <summary>
    /// Resolves a <see cref="Styloagent.Core.Model.ModelTier"/> to a model this machine can ACTUALLY run.
    ///
    /// Tiers map to concrete ids only where those ids are stable. Codex tiers intentionally resolve to its
    /// configured CLI default instead of a guessed model id; explicit Codex model selections are validated
    /// against the live catalog below.
    /// </summary>
    public string? ResolveSupportedModel(Styloagent.Core.Model.AgentRuntimeKind runtime, Styloagent.Core.Model.ModelTier tier)
    {
        var preferred = Styloagent.Core.Model.ModelTierResolver.ResolveModel(runtime, tier);
        if (string.IsNullOrWhiteSpace(preferred)) return null;   // Default tier — the CLI default already
        var agent = Styloagent.Core.Model.AgentRuntime.Name(runtime);
        return Supports(agent, preferred, null) ? preferred : null;
    }

    private AgentCapabilities WithDiscoveredModels(string agent, IReadOnlyList<AgentCapability> models)
    {
        if (models is null || models.Count == 0) return this;
        var list = Agents.ToList();
        int idx = list.FindIndex(a => a.Agent.Equals(agent, StringComparison.OrdinalIgnoreCase));
        var entry = new AgentRuntimeCapabilities(agent, models);
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
