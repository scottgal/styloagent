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
}
