using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public sealed class ModelPolicyTests
{
    [Fact]
    public void Load_matches_job_type_and_preserves_reasoning()
    {
        var path = Path.Combine(Path.GetTempPath(), "model-policy-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, """
            default:
              runtime: codex
              model: gpt-5-codex
              effort: medium
              reasoning: "default code work"
            rules:
              - jobType: architecture
                runtime: claude
                model: opus
                effort: high
                reasoning: "boundary decisions need deeper reasoning"
            """);
        try
        {
            var policy = ModelPolicy.Load(path);
            var architecture = policy.For("ARCHITECTURE");
            var fallback = policy.For("unknown");

            Assert.Equal("opus", architecture.Model);
            Assert.Equal("high", architecture.Effort);
            Assert.Equal("boundary decisions need deeper reasoning", architecture.Reasoning);
            Assert.Equal("gpt-5-codex", fallback.Model);
        }
        finally { File.Delete(path); }
    }
}
