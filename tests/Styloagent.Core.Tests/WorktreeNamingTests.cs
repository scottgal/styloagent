using Styloagent.Core.Git;
using Xunit;

public class WorktreeNamingTests
{
    [Fact]
    public void Derives_path_and_branch_from_prefix()
    {
        var (path, branch) = WorktreeNaming.For("/repo", "foss-", System.Array.Empty<string>());
        Assert.Equal(Path.Combine("/repo", ".worktrees", "foss"), path);
        Assert.Equal("agent/foss", branch);
    }

    [Fact]
    public void Deduplicates_when_path_exists()
    {
        var existing = new[] { Path.Combine("/repo", ".worktrees", "foss") };
        var (path, branch) = WorktreeNaming.For("/repo", "foss-", existing);
        Assert.Equal(Path.Combine("/repo", ".worktrees", "foss-2"), path);
        Assert.Equal("agent/foss-2", branch);
    }
}
