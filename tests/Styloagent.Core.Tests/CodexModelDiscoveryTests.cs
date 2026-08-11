using Styloagent.Core.Mcp;
using Styloagent.Core.Model;

namespace Styloagent.Core.Tests;

/// <summary>
/// The codex model catalog must be DISCOVERED from the machine, exactly as kilo's is, not hard-coded.
///
/// The hard-coded list said "gpt-5-codex" and "gpt-5" — models that no longer exist — while the operator's
/// actual codex default was "gpt-5.6-terra". Because AgentCapabilities.Supports() gates every spawn, asking
/// for a real model the static list had never heard of was REJECTED ("unsupported agent selection"), which is
/// what made "launch codex with terra" unusable. The codex CLI already maintains a live catalog at
/// ~/.codex/models_cache.json, so read that instead of guessing.
/// </summary>
public class CodexModelDiscoveryTests
{
    private const string Catalog = """
    {
      "fetched_at": "2026-08-10T19:39:40.595700Z",
      "models": [
        {
          "slug": "gpt-5.6-sol", "display_name": "GPT-5.6-Sol", "visibility": "list",
          "supported_reasoning_levels": [
            {"effort": "low"}, {"effort": "medium"}, {"effort": "high"},
            {"effort": "xhigh"}, {"effort": "max"}, {"effort": "ultra"}]
        },
        {
          "slug": "gpt-5.6-terra", "display_name": "GPT-5.6-Terra", "visibility": "list",
          "supported_reasoning_levels": [
            {"effort": "low"}, {"effort": "medium"}, {"effort": "high"}, {"effort": "xhigh"}]
        },
        {
          "slug": "gpt-5.6-sol-wm", "display_name": "GPT-5.6-Sol-WM", "visibility": "hide",
          "supported_reasoning_levels": [{"effort": "low"}]
        }
      ]
    }
    """;

    [Fact]
    public void Parses_the_live_codex_catalog()
    {
        var models = CodexModelDiscovery.ParseCatalog(Catalog);

        // "default" (the CLI-default sentinel every runtime advertises) is always offered first.
        Assert.Equal("default", models[0].Id);

        var terra = Assert.Single(models, m => m.Id == "gpt-5.6-terra");
        Assert.Equal("GPT-5.6-Terra", terra.Label);
        Assert.Contains("xhigh", terra.Efforts);
        Assert.Contains("default", terra.Efforts);   // every model accepts the CLI default effort
        Assert.DoesNotContain("ultra", terra.Efforts);

        Assert.Contains(models, m => m.Id == "gpt-5.6-sol");
    }

    [Fact]
    public void Hidden_models_are_not_offered()
        => Assert.DoesNotContain(CodexModelDiscovery.ParseCatalog(Catalog), m => m.Id == "gpt-5.6-sol-wm");

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"models\":[]}")]
    public void Malformed_or_empty_catalog_yields_nothing_so_defaults_apply(string json)
        => Assert.Empty(CodexModelDiscovery.ParseCatalog(json));

    [Fact]
    public void Discovered_models_replace_the_stale_static_codex_list()
    {
        var caps = AgentCapabilities.Load(null).WithCodexModels(CodexModelDiscovery.ParseCatalog(Catalog));

        // The real model the operator actually runs is now spawnable...
        Assert.True(caps.Supports("codex", "gpt-5.6-terra", "high"));
        // ...and the models that no longer exist are gone.
        Assert.False(caps.Supports("codex", "gpt-5-codex", null));
        // Other runtimes are untouched.
        Assert.True(caps.Supports("claude", "opus", "high"));
    }

    [Fact]
    public void An_empty_discovery_leaves_the_static_list_intact()
    {
        var caps = AgentCapabilities.Load(null).WithCodexModels(Array.Empty<AgentCapability>());
        Assert.True(caps.Supports("codex", "default", null));
    }

    /// <summary>
    /// Spawned specialists carry a TIER, not a model id, and ModelTierResolver maps the codex tiers to
    /// hard-coded ids ("gpt-5-codex"/"gpt-5"). Those ids are long dead, so every tier-based codex spawn
    /// resolved to a model this machine does not have — rejected by Supports(), or launched with a
    /// --model the CLI would refuse. A tier must never resolve to a model the machine cannot run: fall
    /// back to the CLI's OWN configured default (what the operator set in ~/.codex/config.toml).
    /// </summary>
    [Theory]
    [InlineData(ModelTier.Opus)]
    [InlineData(ModelTier.Sonnet)]
    [InlineData(ModelTier.Haiku)]
    public void A_tier_never_resolves_to_a_model_this_machine_lacks(ModelTier tier)
    {
        var caps = AgentCapabilities.Load(null).WithCodexModels(CodexModelDiscovery.ParseCatalog(Catalog));

        var model = caps.ResolveSupportedModel(AgentRuntimeKind.Codex, tier);

        Assert.True(caps.Supports("codex", model, null),
            $"tier {tier} resolved to '{model ?? "default"}', which this machine's codex cannot run");
        Assert.NotEqual("gpt-5-codex", model);
    }

    [Fact]
    public void A_tier_that_IS_available_is_still_honoured()
    {
        // Claude's tier ids are real in the static catalog, so tier resolution must pass them through.
        var caps = AgentCapabilities.Load(null);
        Assert.Equal("opus", caps.ResolveSupportedModel(AgentRuntimeKind.Claude, ModelTier.Opus));
        Assert.Equal("sonnet", caps.ResolveSupportedModel(AgentRuntimeKind.Claude, ModelTier.Sonnet));
    }
}
