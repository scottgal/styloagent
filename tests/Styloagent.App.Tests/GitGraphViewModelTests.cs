using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;
using Xunit;

public class GitGraphViewModelTests
{
    private sealed class FakeLog : IGitLog
    {
        public Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(string worktreePath, int limit = 200, CancellationToken ct = default)
        {
            IReadOnlyList<Commit> commits = new List<Commit>
            {
                new() { SHA = "b", Color = 0 },
                new() { SHA = "a", Color = 0 },
            };
            return Task.FromResult(GitResult<IReadOnlyList<Commit>>.Success(commits));
        }
    }

    [Fact]
    public async Task LoadAsync_builds_a_graph_and_counts_commits()
    {
        var vm = new GitGraphViewModel(new FakeLog());
        await vm.LoadAsync("/repo/.worktrees/foss");
        Assert.NotNull(vm.Graph);
        Assert.Equal(2, vm.CommitCount);
    }
}
