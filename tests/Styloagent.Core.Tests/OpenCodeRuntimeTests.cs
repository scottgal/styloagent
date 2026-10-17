using System.Text.Json;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

public class OpenCodeRuntimeTests
{
    private static readonly string[] ExpectedEfforts = ["default", "none", "low", "high", "max"];
    [Theory]
    [InlineData("opencode")]
    [InlineData("OpenCode")]
    [InlineData("open-code")]
    public void Runtime_names_roundtrip_without_falling_back_to_Claude(string name)
    {
        Assert.Equal(AgentRuntimeKind.OpenCode, AgentRuntime.Parse(name));
        Assert.Equal("opencode", AgentRuntime.Name(AgentRuntime.Parse(name)));
        Assert.Throws<ArgumentException>(() => AgentRuntime.Parse("unavailable-runtime"));
    }

    [Fact]
    public void Catalog_filters_unusable_models_and_preserves_native_variants()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[
              {"id":"deepseek-flash","providerID":"deepseek","name":"Flash","enabled":true,
               "capabilities":{"tools":true,"output":["text"]},"limit":{"context":1048576},
               "variants":[{"id":"none"},{"id":"low"},{"id":"high"},{"id":"max"}]},
              {"id":"disabled","enabled":false},
              {"id":"image","enabled":true,"capabilities":{"tools":true,"output":["image"]}},
              {"id":"no-tools","enabled":true,"capabilities":{"tools":false,"output":["text"]}}
            ]}
            """);
        var model = Assert.Single(OpenCodeModelCatalog.Parse(doc.RootElement));
        Assert.Equal("deepseek/deepseek-flash", model.QualifiedId);
        Assert.Equal(1048576, model.ContextTokens);
        Assert.Equal(ExpectedEfforts, model.Capability.Efforts);
        Assert.DoesNotContain("medium", model.Capability.Efforts);
    }
    private static OpenCodeModel NativeModel(string provider, string id) => new(id, provider, id, [], 1_000_000);

    [Fact]
    public void Default_model_is_direct_DeepSeek_Flash_instead_of_an_unrelated_global_default()
    {
        var flash = NativeModel("deepseek", "deepseek-flash");
        Assert.Equal(flash, OpenCodeModelCatalog.DefaultFlash([NativeModel("opencode", "free-model"), flash], false));
    }

    [Fact]
    public void OpenRouter_default_discovers_the_latest_stable_Flash_version_numerically()
    {
        var latest = NativeModel("openrouter", "deepseek/deepseek-v4.10-flash-1017");
        Assert.Equal(latest, OpenCodeModelCatalog.DefaultFlash([
            NativeModel("deepseek", "deepseek-flash"), NativeModel("openrouter", "deepseek/deepseek-v4.9-flash-1017"),
            latest, NativeModel("openrouter", "deepseek/deepseek-v5-flash-preview"),
            NativeModel("openrouter", "deepseek/deepseek-v5-pro")], true));
    }

    [Fact]
    public void Missing_Flash_never_silently_selects_an_unrelated_free_model()
        => Assert.Throws<ArgumentException>(() => OpenCodeModelCatalog.DefaultFlash([NativeModel("opencode", "free-model")], false));

    [Fact]
    public void Configured_OpenRouter_does_not_fall_back_to_the_direct_provider()
        => Assert.Throws<ArgumentException>(() => OpenCodeModelCatalog.DefaultFlash([NativeModel("deepseek", "deepseek-flash")], true));
}
