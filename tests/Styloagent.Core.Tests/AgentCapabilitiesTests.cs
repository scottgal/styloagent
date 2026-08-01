using Styloagent.Core.Mcp;
using Xunit;

public class AgentCapabilitiesTests
{
    [Fact]
    public void Load_missing_file_uses_current_deepcode_fallback_models()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "styloagent-capabilities-tests", Guid.NewGuid().ToString("N"));

        var capabilities = AgentCapabilities.Load(repoRoot);
        var deepcode = Assert.Single(capabilities.Agents, agent => agent.Agent == "deepcode");

        Assert.Equal("default,deepseek-v4-pro,deepseek-v4-flash", string.Join(',', deepcode.Models.Select(model => model.Id)));
        Assert.Equal("CLI default (deepseek-v4-pro)", deepcode.Models[0].Label);
        Assert.DoesNotContain(deepcode.Models, model => model.Id is "deepseek-v4" or "deepseek-v3");
    }
}
