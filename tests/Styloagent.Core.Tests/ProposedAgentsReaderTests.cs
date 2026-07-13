using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class ProposedAgentsReaderTests
{
    [Fact]
    public void Read_parses_the_agents_schema()
    {
        var path = Path.Combine(Path.GetTempPath(), "pa-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n" +
            "  - prefix: foss-\n" +
            "    responsibility: owns the FOSS packages\n" +
            "    dir: .\n" +
            "    launchPrompt: You are foss-.\n" +
            "  - prefix: dash-\n" +
            "    responsibility: owns the UI\n" +
            "    dir: src/ui\n" +
            "    launchPrompt: You are dash-.\n");
        try
        {
            var agents = ProposedAgentsReader.Read(path);
            Assert.Equal(2, agents.Count);
            Assert.Equal("foss-", agents[0].Prefix);
            Assert.Equal("owns the FOSS packages", agents[0].Responsibility);
            Assert.Equal("src/ui", agents[1].Dir);
            Assert.Equal("You are dash-.", agents[1].LaunchPrompt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_returns_empty_for_missing_or_invalid()
    {
        Assert.Empty(ProposedAgentsReader.Read("/no/such/file.yaml"));

        var bad = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(bad, "this: [is not: valid");
        try { Assert.Empty(ProposedAgentsReader.Read(bad)); }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void Read_parses_the_worktree_flag_and_defaults_it_false()
    {
        var path = Path.Combine(Path.GetTempPath(), "pa-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n" +
            "  - prefix: iso-\n" +
            "    responsibility: overlaps foss\n" +
            "    dir: .\n" +
            "    worktree: true\n" +
            "    launchPrompt: You are iso-.\n" +
            "  - prefix: share-\n" +
            "    responsibility: shares the repo\n" +
            "    dir: .\n" +
            "    launchPrompt: You are share-.\n");   // no worktree key → defaults false
        try
        {
            var agents = ProposedAgentsReader.Read(path);
            Assert.Equal(2, agents.Count);
            Assert.True(agents[0].Worktree);
            Assert.False(agents[1].Worktree);
        }
        finally { File.Delete(path); }
    }
}
