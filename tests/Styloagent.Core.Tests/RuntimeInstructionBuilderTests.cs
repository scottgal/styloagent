using Styloagent.Core.Projects;
using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

public class RuntimeInstructionBuilderTests
{
    [Fact]
    public void Local_overlay_precedes_the_current_canonical_runtime_base()
    {
        var instructions = RuntimeInstructionBuilder.Build("LOCAL STALE RULE", "LOCAL PROTOCOL", "/repos/alpha", "alpha-");

        Assert.True(instructions.IndexOf("LOCAL STALE RULE", StringComparison.Ordinal)
                    < instructions.IndexOf(DefaultTemplates.SystemPrompt, StringComparison.Ordinal));
        Assert.True(instructions.IndexOf("LOCAL PROTOCOL", StringComparison.Ordinal)
                    < instructions.IndexOf(DefaultTemplates.Protocol, StringComparison.Ordinal));
        Assert.True(instructions.IndexOf("Repository root: /repos/alpha", StringComparison.Ordinal)
                    < instructions.IndexOf(DefaultTemplates.SystemPrompt, StringComparison.Ordinal));
    }

    [Fact]
    public void Dynamic_identity_is_scoped_to_each_repository()
    {
        var alpha = RuntimeInstructionBuilder.Build(null, null, "/repos/alpha", "alpha-");
        var beta = RuntimeInstructionBuilder.Build(null, null, "/repos/beta", "beta-");

        Assert.Contains("Repository root: /repos/alpha", alpha);
        Assert.Contains("Overview prefix: alpha-", alpha);
        Assert.DoesNotContain("/repos/beta", alpha);
        Assert.Contains("Repository root: /repos/beta", beta);
        Assert.Contains("Overview prefix: beta-", beta);
    }
}
