using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Git;

public class ChangesViewModelTests
{
    private sealed class FakeGit : IGitService
    {
        public Task<GitResult<GitStatus>> GetStatusAsync(string w, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(new GitStatus(true, 0, 0, false,
                new[]
                {
                    new GitChange("staged.txt",   GitChangeKind.Modified, Staged: true,  Unstaged: false),
                    new GitChange("unstaged.txt", GitChangeKind.Added,    Staged: false, Unstaged: true),
                })));

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
        public string? LastStaged;
        public string? LastCommitMsg;
        public bool NextFails;

        public Task<GitResult> StageAsync(string w, string p, CancellationToken ct = default)
        {
            LastStaged = p;
            return Task.FromResult(NextFails ? GitResult.Fail("boom") : GitResult.Success());
        }

        public Task<GitResult> UnstageAsync(string w, string p, CancellationToken ct = default)
            => Task.FromResult(NextFails ? GitResult.Fail("boom") : GitResult.Success());

        public Task<GitResult> CommitAsync(string w, string m, CancellationToken ct = default)
        {
            LastCommitMsg = m;
            return Task.FromResult(NextFails ? GitResult.Fail("boom") : GitResult.Success());
        }

        public Task<GitResult> PushAsync(string w, CancellationToken ct = default)
            => Task.FromResult(NextFails ? GitResult.Fail("boom") : GitResult.Success());

        public Task<GitResult> PullAsync(string w, CancellationToken ct = default)
            => Task.FromResult(NextFails ? GitResult.Fail("boom") : GitResult.Success());
    }

    // ── existing test (updated to 3-arg ctor) ────────────────────────────────

    [Fact]
    public async Task Load_lists_files_and_selecting_one_loads_its_diff()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite());
        await vm.LoadAsync("/wt");
        Assert.Equal(2, vm.Files.Count);

        await vm.SelectFileAsync(vm.Files[0]);
        Assert.NotNull(vm.Diff.File);
        Assert.Contains(vm.Diff.File!.Lines, l => l.Content == "hello");
    }

    // ── staged / unstaged sections ───────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_splits_files_into_staged_and_unstaged_sections()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite());
        await vm.LoadAsync("/wt");

        Assert.Equal(1, vm.StagedFiles.Count);
        Assert.Equal(1, vm.UnstagedFiles.Count);
        Assert.Equal("staged.txt",   vm.StagedFiles[0].Path);
        Assert.Equal("unstaged.txt", vm.UnstagedFiles[0].Path);
        Assert.Equal(2, vm.Files.Count);
    }

    // ── CanCommit ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CanCommit_false_when_message_empty()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite());
        await vm.LoadAsync("/wt");
        vm.CommitMessage = "";
        Assert.False(vm.CanCommit);
    }

    [Fact]
    public async Task CanCommit_true_when_staged_files_and_message_present()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite());
        await vm.LoadAsync("/wt");
        vm.CommitMessage = "my commit";
        Assert.True(vm.CanCommit);
    }

    // ── StageAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StageAsync_calls_write_with_correct_path_and_reloads()
    {
        var write = new FakeWrite();
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), write);
        await vm.LoadAsync("/wt");

        var unstaged = vm.UnstagedFiles[0];
        await vm.StageAsync(unstaged);

        Assert.Equal("unstaged.txt", write.LastStaged);
        // after reload the collections are repopulated (FakeGit always returns same set)
        Assert.Equal(1, vm.StagedFiles.Count);
        Assert.Equal(1, vm.UnstagedFiles.Count);
    }

    // ── CommitAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_calls_write_with_message_and_clears_it_on_success()
    {
        var write = new FakeWrite();
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), write);
        await vm.LoadAsync("/wt");
        vm.CommitMessage = "feat: new thing";

        await vm.CommitAsync();

        Assert.Equal("feat: new thing", write.LastCommitMsg);
        Assert.Equal("", vm.CommitMessage);
    }

    [Fact]
    public async Task CommitAsync_does_nothing_when_CanCommit_false()
    {
        var write = new FakeWrite();
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), write);
        await vm.LoadAsync("/wt");
        vm.CommitMessage = ""; // CanCommit == false

        await vm.CommitAsync();

        Assert.Null(write.LastCommitMsg);
    }

    // ── Clear ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_empties_all_collections_and_resets_commit_message()
    {
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), new FakeWrite());
        await vm.LoadAsync("/wt");
        vm.CommitMessage = "wip";

        vm.Clear();

        Assert.Empty(vm.Files);
        Assert.Empty(vm.StagedFiles);
        Assert.Empty(vm.UnstagedFiles);
        Assert.Equal("", vm.CommitMessage);
    }

    // ── WriteError ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Failed_write_op_surfaces_the_error_then_clears_on_success()
    {
        var write = new FakeWrite { NextFails = true };
        var vm = new ChangesViewModel(new FakeGit(), new FakeDiff(), write);
        await vm.LoadAsync("/wt");

        await vm.PushAsync();
        Assert.True(vm.HasWriteError);
        Assert.Equal("boom", vm.WriteError);

        write.NextFails = false;
        await vm.PullAsync();
        Assert.False(vm.HasWriteError);
        Assert.Null(vm.WriteError);
    }
}
