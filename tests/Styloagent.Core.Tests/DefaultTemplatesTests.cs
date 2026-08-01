using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class DefaultTemplatesTests
{
    [Fact]
    public void SystemPrompt_uses_direct_governed_spawning_without_a_staging_file()
    {
        Assert.Contains("`spawn_agent` directly", DefaultTemplates.SystemPrompt);
        Assert.DoesNotContain("proposed-agents.yaml", DefaultTemplates.SystemPrompt);
    }

    [Theory]
    [InlineData("## Execution discipline")]
    [InlineData("## Credentials and environments")]
    [InlineData("## Completion gate")]
    [InlineData("Production is forbidden unless the operator explicitly says `prod`")]
    public void Agent_contract_includes_the_non_negotiable_operating_rules(string text)
    {
        Assert.Contains(text, DefaultTemplates.SystemPrompt);
        Assert.Contains(text, DefaultTemplates.Protocol);
    }

    [Theory]
    [InlineData("## Per-agent saved-context (required)")]
    [InlineData("## Fleet roster and scope adjacency")]
    [InlineData("Never put secret values")]
    [InlineData("closest")]
    public void Protocol_seeds_saved_context_discipline_and_roster_adjacency(string text)
    {
        Assert.Contains(text, DefaultTemplates.Protocol);
    }
}
