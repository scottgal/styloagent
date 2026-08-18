using Styloagent.Core.Mcp;
using Xunit;

public class AgentCapabilitiesTests
{
    private static readonly string[] HighEfforts = { "default", "high" };
    [Fact]
    public void Load_missing_file_never_advertises_kilo_or_dead_codex_models()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "styloagent-capabilities-tests", Guid.NewGuid().ToString("N"));

        var capabilities = AgentCapabilities.Load(repoRoot);

        Assert.Contains(capabilities.Agents, agent => agent.Agent == "claude");
        Assert.Contains(capabilities.Agents, agent => agent.Agent == "codex");
        Assert.DoesNotContain(capabilities.Agents, agent => agent.Agent == "kilo");
        Assert.DoesNotContain(capabilities.Agents, agent => agent.Agent == "deepcode");
        Assert.False(capabilities.Supports("codex", "gpt-5", "medium"));
        Assert.False(capabilities.Supports("codex", "gpt-5-codex", "medium"));
    }

    [Fact]
    public void Supports_rejects_kilo_even_when_a_repository_catalog_advertises_it()
    {
        var capabilities = AgentCapabilities.Load(null);
        var advertised = capabilities with
        {
            Agents = capabilities.Agents.Append(new AgentRuntimeCapabilities("kilo", new[]
            {
                new AgentCapability("default", "Kilo default", HighEfforts),
            })).ToList(),
        };

        Assert.False(advertised.Supports("kilo", "default", "high"));
    }
}
