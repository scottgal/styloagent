using Styloagent.Core.Mcp;
using Xunit;

public class AgentCapabilitiesTests
{
    private static readonly string[] HighEfforts = { "default", "high" };
    private static readonly string[] MediumEfforts = { "default", "medium" };

    [Fact]
    public void Load_missing_file_uses_current_fallback_models()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "styloagent-capabilities-tests", Guid.NewGuid().ToString("N"));

        var capabilities = AgentCapabilities.Load(repoRoot);

        Assert.Contains(capabilities.Agents, agent => agent.Agent == "claude");
        Assert.Contains(capabilities.Agents, agent => agent.Agent == "codex");
        Assert.Contains(capabilities.Agents, agent => agent.Agent == "kilo");
        Assert.DoesNotContain(capabilities.Agents, agent => agent.Agent == "deepcode");

        var kilo = Assert.Single(capabilities.Agents, agent => agent.Agent == "kilo");
        Assert.Equal("default,kilo/deepseek/deepseek-v4-pro,kilo/deepseek/deepseek-v4-flash",
            string.Join(',', kilo.Models.Select(model => model.Id)));
        Assert.Equal("DeepSeek V4 Pro (overview default)", kilo.Models[0].Label);
        Assert.DoesNotContain(kilo.Models, model => model.Id is "deepseek-v4" or "deepseek-v3");
    }

    [Fact]
    public void WithKiloModels_replaces_the_kilo_runtime_entry()
    {
        var capabilities = AgentCapabilities.Load(null);
        var live = new[]
        {
            new AgentCapability("kilo/deepseek/deepseek-v4-pro", "DeepSeek V4 Pro", HighEfforts),
            new AgentCapability("kilo/anthropic/claude-sonnet-4.6", "Claude Sonnet", MediumEfforts),
        };

        var merged = capabilities.WithKiloModels(live);

        var kilo = Assert.Single(merged.Agents, agent => agent.Agent == "kilo");
        Assert.Equal(2, kilo.Models.Count);
        Assert.Equal("kilo/anthropic/claude-sonnet-4.6", kilo.Models[1].Id);
        Assert.True(merged.Supports("kilo", "kilo/deepseek/deepseek-v4-pro", "high"));
        Assert.False(merged.Supports("kilo", "kilo/deepseek/deepseek-v4-flash", "default"));
    }

    [Fact]
    public void WithKiloModels_keeps_catalog_when_live_list_is_empty()
    {
        var capabilities = AgentCapabilities.Load(null);
        var merged = capabilities.WithKiloModels(Array.Empty<AgentCapability>());

        Assert.Equal(capabilities, merged);
    }
}
