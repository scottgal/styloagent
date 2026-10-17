using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;
using Xunit;

public class GitGraphViewModelTests
{
    private sealed class DelayedLog : IGitLog
    {
        public readonly TaskCompletionSource<GitResult<IReadOnlyList<Commit>>> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(string path, int limit = 200, CancellationToken ct = default)
            => path == "/old" ? Pending.Task : new FakeLog().GetCommitsAsync(path, limit, ct);
    }

    [Fact]
    public async Task An_old_history_read_cannot_overwrite_a_new_selection_or_a_clear()
    {
        var log = new DelayedLog();
        var vm = new GitGraphViewModel(log);
        var old = vm.LoadAsync("/old");
        await vm.LoadAsync("/new");
        vm.Clear();
        log.Pending.SetResult(GitResult<IReadOnlyList<Commit>>.Success(new List<Commit> { new() { SHA = "stale" } }));
        await old;
        Assert.Null(vm.Graph);
        Assert.Empty(vm.Commits);
    }

    private sealed class FakeLog : IGitLog
    {
        public Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(string worktreePath, int limit = 200, CancellationToken ct = default)
        {
            IReadOnlyList<Commit> commits = new List<Commit>
            {
                new()
                {
                    SHA = "b", Color = 0,
                    Decorators = new List<Decorator> { new() { Type = DecoratorType.Tag, Name = "v2.4.8" } },
                },
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

    [Fact]
    public async Task LoadAsync_exposes_tag_labels_on_the_commit_timeline()
    {
        var vm = new GitGraphViewModel(new FakeLog());
        await vm.LoadAsync("/repo/.worktrees/foss");

        Assert.Equal("v2.4.8", vm.Commits[0].TagsText);
        Assert.Equal("", vm.Commits[1].TagsText);
    }
}
