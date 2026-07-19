using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class DefaultTemplatesTests
{
    // Note: "worktree:" alone already appears in the spawn_agent parameter docs, so asserting the bare
    // key would pass vacuously. The real gap (design §2.1) is the proposed-agents YAML *schema block*,
    // which must teach the field with its default — "worktree: false" is unique to that block.
    [Fact]
    public void SystemPrompt_proposal_schema_teaches_the_worktree_field()
        => Assert.Contains("worktree: false", DefaultTemplates.SystemPrompt);

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
}
