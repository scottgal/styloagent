using System.Text.Json;
using Styloagent.Core.Hooks;
using Styloagent.Core.Mcp;
using Styloagent.Core.Sessions;

public class DeepSeekEndpointTests
{
    [Theory]
    [InlineData("https://api.deepseek.com/anthropic", false)]
    [InlineData("https://openrouter.ai/api", true)]
    public void Capabilities_and_picker_only_offer_ids_for_the_selected_provider(string endpoint, bool router)
    {
        var root = Path.Combine(Path.GetTempPath(), "deepseek-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".styloagent"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".styloagent", "deepseek.env"), "ANTHROPIC_BASE_URL=" + endpoint);
            var capabilities = AgentCapabilities.Load(root).WithDeepSeekEndpoint(endpoint);
            Assert.Equal(router, capabilities.Supports("claude-deepseek", DeepSeekModels.OpenRouterFlash, "default"));
            var json = HookSettings.BuildSettingsJson("test-", "/tmp/hooks", repoRoot: root, includeDeepSeekModelPicker: true);
            using var document = JsonDocument.Parse(json);
            var picker = document.RootElement.GetProperty("modelPicker");
            Assert.True(picker.GetProperty("replaceBuiltInOptions").GetBoolean());
            var rows = picker.GetProperty("options").EnumerateArray().ToArray();
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.Equal(router, row.GetProperty("model").GetString()!.Contains('/', StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Explicit_incompatible_manifest_models_are_rejected_before_launch()
    {
        Assert.Throws<ArgumentException>(() => DeepSeekModels.ResolveForEndpoint(DeepSeekModels.OpenRouterFlash, "https://api.deepseek.com/anthropic"));
        Assert.Throws<ArgumentException>(() => DeepSeekModels.ResolveForEndpoint("deepseek-reasoner", "https://openrouter.ai/api"));
    }
}
