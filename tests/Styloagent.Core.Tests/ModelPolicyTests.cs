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

    /// <summary>
    /// A policy row that names NO runtime/model must stay silent on both, so the spawn path falls back to
    /// "inherit the overview's runtime, one tier down for the model". Job-type policy is about how DEEP to
    /// reason, not which CLI to run — pinning a runtime here is what forced every spawn onto kilo.
    /// </summary>
    [Fact]
    public void A_policy_that_names_no_runtime_or_model_forces_neither()
    {
        var path = Path.Combine(Path.GetTempPath(), "model-policy-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, """
            default:
              reasoning: "inherit the overview's runtime; step the model down one tier"
            rules:
              - jobType: architecture
                reasoning: "boundary decisions deserve the deeper end of whatever runtime is in play"
            """);
        try
        {
            var policy = ModelPolicy.Load(path);
            foreach (var selection in new[] { policy.For("architecture"), policy.For("anything-else") })
            {
                Assert.Null(selection.Runtime);
                Assert.Null(selection.Model);
                Assert.NotEmpty(selection.Reasoning);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The BUNDLED policy must never pin a runtime or model. It shipped pinning every job type to one
    /// runtime, which the architect then obeyed over the human's explicit request ("launch codex" produced
    /// kilo). Runtime is inherited from the overview and the model steps down a tier; the template only
    /// carries the human-readable reasoning.
    /// </summary>
    [Fact]
    public void The_bundled_policy_template_pins_no_runtime_or_model()
    {
        var path = Path.Combine(Path.GetTempPath(), "model-policy-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, Styloagent.Core.Projects.DefaultTemplates.ModelPolicy);
        try
        {
            var policy = ModelPolicy.Load(path);

            Assert.Null(policy.Default.Runtime);
            Assert.Null(policy.Default.Model);
            Assert.NotEmpty(policy.Default.Reasoning);

            Assert.NotEmpty(policy.Rules);
            foreach (var (jobType, selection) in policy.Rules)
            {
                Assert.True(selection.Runtime is null, $"job type '{jobType}' pins runtime '{selection.Runtime}'");
                Assert.True(selection.Model is null, $"job type '{jobType}' pins model '{selection.Model}'");
                Assert.NotEmpty(selection.Reasoning);
            }
        }
        finally { File.Delete(path); }
    }
}
