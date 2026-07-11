using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;
using Xunit;

namespace Styloagent.App.Tests;

public class GitPanelRefreshTests
{
    private sealed class FakeLog : IGitLog
    {
        public Task<GitResult<System.Collections.Generic.IReadOnlyList<Commit>>> GetCommitsAsync(string w, int limit = 200, CancellationToken ct = default)
        {
            System.Collections.Generic.IReadOnlyList<Commit> c = new[] { new Commit { SHA = "a", Color = 0 } };
            return Task.FromResult(GitResult<System.Collections.Generic.IReadOnlyList<Commit>>.Success(c));
        }
    }

    private sealed class FakeGit : IGitService
    {
        public Task<GitResult<GitStatus>> GetStatusAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(new GitStatus(true, 0, 0, false,
                new[] { new GitChange("a.txt", GitChangeKind.Modified, false, true), new GitChange("b.txt", GitChangeKind.Added, true, false) })));

        public Task<GitResult> AddWorktreeAsync(string r, string w, string b, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> RemoveWorktreeAsync(string r, string w, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> MergeNoFfAsync(string r, string s, string i, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> AbortMergeAsync(string r, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> DeleteBranchAsync(string r, string b, bool f, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeDiff : IGitDiff
    {
        public Task<GitResult<FileDiff>> GetDiffAsync(string w, string path, bool staged, CancellationToken ct = default)
            => Task.FromResult(GitResult<FileDiff>.Success(new FileDiff(path, 1, 0, false,
                new[] { new DiffLine(DiffLineKind.Added, "hello", 0, 1) })));
    }

    private sealed class FakeWrite : IGitWrite
    {
        public Task<GitResult> StageAsync(string w, string p, CancellationToken ct = default)   => Task.FromResult(GitResult.Success());
        public Task<GitResult> UnstageAsync(string w, string p, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> CommitAsync(string w, string m, CancellationToken ct = default)  => Task.FromResult(GitResult.Success());
        public Task<GitResult> PushAsync(string w, CancellationToken ct = default)              => Task.FromResult(GitResult.Success());
        public Task<GitResult> PullAsync(string w, CancellationToken ct = default)              => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeBranch : IGitBranch
    {
        public Task<GitResult<IReadOnlyList<GitBranch>>> ListBranchesAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<IReadOnlyList<GitBranch>>.Success(
                new List<GitBranch> { new GitBranch("main", IsCurrent: true) }));

        public Task<GitResult> CreateBranchAsync(string w, string name, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> SwitchBranchAsync(string w, string name, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());
    }

    private sealed class FakeStash : IGitStash
    {
        public Task<GitResult> StashAsync(string w, string? message, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult> StashPopAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult.Success());

        public Task<GitResult<IReadOnlyList<string>>> ListStashesAsync(string w, CancellationToken ct = default)
        {
            IReadOnlyList<string> list = Array.Empty<string>();
            return Task.FromResult(GitResult<IReadOnlyList<string>>.Success(list));
        }
    }

    [Fact]
    public async Task GitGraph_Clear_blanks_the_graph()
    {
        var vm = new GitGraphViewModel(new FakeLog());
        await vm.LoadAsync("/wt");
        Assert.NotNull(vm.Graph);
        vm.Clear();
        Assert.Null(vm.Graph);
        Assert.Equal(0, vm.CommitCount);
    }

    [Fact]
    public async Task Changes_Clear_empties_files_and_diff()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite(), new FakeBranch(), new FakeStash());
        await vm.LoadAsync("/wt");
        Assert.Equal(2, vm.Files.Count);
        await vm.SelectFileAsync(vm.Files[0]);
        Assert.NotNull(vm.Diff.File);

        vm.Clear();

        Assert.Empty(vm.Files);
        Assert.Null(vm.Diff.File);
    }

    /// <summary>
    /// After a merge removes a worktree (WorktreePath becomes null), RefreshGitPanelFor clears
    /// both the History graph and the Changes list synchronously — the same code path WrapUp now
    /// calls when the wrapped pane is the selected pane.
    /// </summary>
    [Fact]
    public async Task RefreshGitPanelFor_NullWorktreePath_ClearsGraphAndChanges()
    {
        // Build the sub-VMs with data so we can verify they get cleared.
        var graph = new GitGraphViewModel(new FakeLog());
        await graph.LoadAsync("/some-worktree");
        Assert.NotNull(graph.Graph); // precondition: data loaded

        var changes = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite(), new FakeBranch(), new FakeStash());
        await changes.LoadAsync("/some-worktree");
        Assert.NotEmpty(changes.Files); // precondition: data loaded

        // Set up a channel root and get a MainWindowViewModel instance.
        var channelRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var savedContext = Path.Combine(channelRoot, "saved-context");
        Directory.CreateDirectory(savedContext);
        File.WriteAllText(Path.Combine(savedContext, "agent-context.md"), "# agent");
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                channelRoot, new FakeLauncher(), new FakeWatcher());

            // Wire the pre-loaded sub-VMs into the main VM.
            vm.GitGraph = graph;
            vm.Changes = changes;

            // Pane.WorktreePath is null by default (no worktree was assigned at init);
            // this mirrors the post-merge state that WrapUp now produces.
            Assert.Null(vm.Pane!.WorktreePath);

            // Act — the selected pane has no worktree; RefreshGitPanelFor takes the sync else-branch.
            vm.RefreshGitPanelFor(vm.Pane);

            // Assert — graph and changes are cleared.
            Assert.Null(vm.GitGraph.Graph);
            Assert.Equal(0, vm.GitGraph.CommitCount);
            Assert.Empty(vm.Changes.Files);
        }
        finally
        {
            if (Directory.Exists(channelRoot))
                Directory.Delete(channelRoot, recursive: true);
        }
    }
}
