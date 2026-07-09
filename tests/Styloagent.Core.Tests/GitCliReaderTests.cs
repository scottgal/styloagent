using Styloagent.Terminal;
using Xunit;

public class GitCliReaderTests
{
    [Fact]
    public void Parse_reads_multiple_worktrees_with_branches_and_detached()
    {
        var porcelain =
            "worktree /repo/main\nHEAD aaa111\nbranch refs/heads/main\n\n" +
            "worktree /repo/wt-feature\nHEAD bbb222\nbranch refs/heads/claude/atoms\n\n" +
            "worktree /repo/wt-detached\nHEAD ccc333\ndetached\n\n";

        var wts = GitCliReader.Parse(porcelain);

        Assert.Equal(3, wts.Count);
        Assert.Equal("/repo/main", wts[0].Path);
        Assert.Equal("main", wts[0].Branch);
        Assert.Equal("aaa111", wts[0].Head);
        Assert.Equal("claude/atoms", wts[1].Branch);
        Assert.Null(wts[2].Branch);                 // detached
        Assert.Equal("wt-detached", wts[2].Name);   // Name falls back to the dir segment
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Lists_worktrees_of_a_real_repo()
    {
        // The test runs from inside the styloagent git repo's bin dir → git finds the repo.
        var reader = new GitCliReader();
        var wts = await reader.ListWorktreesAsync(Directory.GetCurrentDirectory());

        Assert.NotEmpty(wts);
        Assert.All(wts, w => Assert.True(Directory.Exists(w.Path), $"worktree path should exist: {w.Path}"));
    }

    [Fact]
    public async Task Non_repo_directory_returns_empty_not_throw()
    {
        var reader = new GitCliReader();
        var tmp = Path.Combine(Path.GetTempPath(), $"not-a-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var wts = await reader.ListWorktreesAsync(tmp);
            Assert.Empty(wts);
        }
        finally { Directory.Delete(tmp, true); }
    }
}
