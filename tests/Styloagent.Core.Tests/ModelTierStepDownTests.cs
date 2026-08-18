using Styloagent.Core.Model;

namespace Styloagent.Core.Tests;

/// <summary>
/// A spawned agent takes the overview's runtime and ONE TIER DOWN for its model — the fleet's standing rule,
/// so job-type policy never has to name a CLI or a model id (both of which rot, and both of which used to
/// override what the human actually asked for). The step is expressed in TIERS, not model ids, so it holds
/// on every runtime.
/// </summary>
public class ModelTierStepDownTests
{
    [Theory]
    [InlineData(ModelTier.Opus, ModelTier.Sonnet)]
    [InlineData(ModelTier.Sonnet, ModelTier.Haiku)]
    public void A_child_runs_one_tier_below_its_parent(ModelTier parent, ModelTier expected)
        => Assert.Equal(expected, ModelTierResolver.StepDown(parent));

    [Fact]
    public void The_cheapest_tier_is_the_floor_so_a_deep_tree_never_underflows()
        => Assert.Equal(ModelTier.Haiku, ModelTierResolver.StepDown(ModelTier.Haiku));

    /// <summary>
    /// "Default" means the runtime's own flagship (whatever the CLI is configured for), so one step down
    /// from it is the standard tier — the same choice spawned specialists already got.
    /// </summary>
    [Fact]
    public void Stepping_down_from_the_CLI_default_lands_on_the_standard_tier()
        => Assert.Equal(ModelTier.Sonnet, ModelTierResolver.StepDown(ModelTier.Default));

    [Fact]
    public void Stepping_down_resolves_to_a_stable_model_for_tiered_runtimes()
    {
        foreach (var runtime in new[] { AgentRuntimeKind.Claude, AgentRuntimeKind.ClaudeDeepSeek })
        {
            var child = ModelTierResolver.StepDown(ModelTier.Opus);
            Assert.NotNull(ModelTierResolver.ResolveModel(runtime, child));
        }
    }

    [Fact]
    public void Codex_tiers_use_the_live_cli_default_instead_of_a_guessed_model_id()
        => Assert.Null(ModelTierResolver.ResolveModel(AgentRuntimeKind.Codex, ModelTier.Sonnet));
}
