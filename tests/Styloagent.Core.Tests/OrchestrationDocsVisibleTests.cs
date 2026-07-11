using Styloagent.Core.Docs;
using Xunit;

/// <summary>
/// Guards that the overview agent's living docs (.styloagent/spec.md, architecture.md) surface in the
/// DocLibrary — otherwise the spec-first orchestration would be invisible in the cockpit.
/// </summary>
public class OrchestrationDocsVisibleTests
{
    [Fact]
    public void Spec_and_architecture_under_dot_styloagent_are_listed()
    {
        var root = Path.Combine(Path.GetTempPath(), "orch-docs-" + Guid.NewGuid().ToString("N"));
        var cfg = Path.Combine(root, ".styloagent");
        Directory.CreateDirectory(cfg);
        try
        {
            File.WriteAllText(Path.Combine(root, "README.md"), "# Readme");
            File.WriteAllText(Path.Combine(cfg, "spec.md"), "# Spec");
            File.WriteAllText(Path.Combine(cfg, "architecture.md"), "# Architecture\n\n```mermaid\nC4Context\n```\n");

            var entries = DocLibraryReader.Read(root, channelRoot: null);
            var names = entries.Select(e => e.Title).ToList();

            Assert.Contains("spec.md", names);
            Assert.Contains("architecture.md", names);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
