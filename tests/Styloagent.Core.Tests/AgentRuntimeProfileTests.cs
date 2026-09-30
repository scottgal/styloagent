using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

public class AgentRuntimeProfileTests
{
    [Fact]
    public void Codex_default_or_tier_launch_uses_the_account_default_instead_of_a_stale_model_id()
    {
        var profile = AgentRuntimeProfile.For(AgentRuntimeKind.Codex);

        Assert.Empty(profile.ModelEffortArgs(null, null));
        Assert.Empty(profile.ModelEffortArgs(null, null, ModelTier.Opus));
    }

    [Fact]
    public void Codex_explicit_live_model_and_effort_are_preserved_exactly()
    {
        var args = AgentRuntimeProfile.For(AgentRuntimeKind.Codex)
            .ModelEffortArgs("gpt-5.6-luna", "medium", ModelTier.Opus);

        Assert.Equal(["--model", "gpt-5.6-luna", "--config", "model_reasoning_effort=\"medium\""], args);
    }

    [Fact]
    public void Claude_DeepSeek_explicit_effort_is_still_passed_at_launch()
    {
        var args = AgentRuntimeProfile.For(AgentRuntimeKind.ClaudeDeepSeek)
            .ModelEffortArgs("deepseek-flash", "medium", ModelTier.Opus);

        Assert.Equal(["--model", "deepseek-flash", "--effort", "medium"], args);
    }

    [Fact]
    public void Removed_runtime_cannot_be_launched_as_claude_by_accident()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AgentRuntimeProfile.For(AgentRuntimeKind.Kilo));
}
