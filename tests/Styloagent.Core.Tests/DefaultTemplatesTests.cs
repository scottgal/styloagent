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
}
