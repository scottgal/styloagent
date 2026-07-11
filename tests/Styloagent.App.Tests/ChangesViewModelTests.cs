using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;

public class ChangesViewModelTests
{
    private sealed class FakeGit : IGitService
    {
        public Task<GitResult<GitStatus>> GetStatusAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(new GitStatus(true, 0, 0, false,
                new[] { new GitChange("a.txt", GitChangeKind.Modified), new GitChange("b.txt", GitChangeKind.Added) })));

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

    [Fact]
    public async Task Load_lists_files_and_selecting_one_loads_its_diff()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff());
        await vm.LoadAsync("/wt");
        Assert.Equal(2, vm.Files.Count);

        await vm.SelectFileAsync(vm.Files[0]);
        Assert.NotNull(vm.Diff.File);
        Assert.Contains(vm.Diff.File!.Lines, l => l.Content == "hello");
    }
}
