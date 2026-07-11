using Styloagent.Core.Git;
using Xunit;

public class GitBadgeTests
{
    [Fact]
    public void No_worktree_has_no_badge()
        => Assert.Equal("", GitBadge.Format(null, hasWorktree: false));

    [Fact]
    public void Clean_worktree_shows_a_tick()
        => Assert.Equal("✓", GitBadge.Format(GitStatus.Clean, hasWorktree: true));

    [Fact]
    public void Ahead_behind_and_dirty_compose()
    {
        var s = new GitStatus(true, 3, 1, false, System.Array.Empty<GitChange>());
        Assert.Equal("↑3 ↓1 ✎", GitBadge.Format(s, hasWorktree: true));
    }

    [Fact]
    public void Conflict_is_flagged()
    {
        var s = new GitStatus(true, 0, 0, true, System.Array.Empty<GitChange>());
        Assert.Contains("⚠", GitBadge.Format(s, hasWorktree: true));
    }
}
