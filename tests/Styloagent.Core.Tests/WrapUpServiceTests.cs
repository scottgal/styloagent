using Styloagent.Core.Git;
using Styloagent.Core.Issues;
using Styloagent.Core.Projects;
using Xunit;

public class WrapUpServiceTests
{
    private sealed class FakeGit : IGitService
    {
        public GitStatus Status = GitStatus.Clean;
        public bool MergeOk = true;
        public bool Removed, BranchDeleted, MergeAborted;
        public bool StatusOk = true;

        public Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
            => Task.FromResult(StatusOk ? GitResult<GitStatus>.Success(Status) : GitResult<GitStatus>.Fail("git status failed"));
        public Task<GitResult> AddWorktreeAsync(string r, string w, string b, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> RemoveWorktreeAsync(string r, string w, CancellationToken ct = default) { Removed = true; return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> MergeNoFfAsync(string r, string s, string i, CancellationToken ct = default)
            => Task.FromResult(MergeOk ? GitResult.Success() : GitResult.Fail("CONFLICT (content): a.txt"));
        public Task<GitResult> AbortMergeAsync(string r, CancellationToken ct = default) { MergeAborted = true; return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> DeleteBranchAsync(string r, string b, bool f, CancellationToken ct = default) { BranchDeleted = true; return Task.FromResult(GitResult.Success()); }
    }

    private sealed class FakeTests : ITestRunner
    {
        public bool Pass = true;
        public Task<TestOutcome> RunAsync(string dir, string cmd, CancellationToken ct = default)
            => Task.FromResult(new TestOutcome(Pass, Pass ? "ok" : "FAILED: 1 test"));
    }

    private static (WrapUpRequest req, string issues) Fixture()
        => (new WrapUpRequest("foss-", "/repo", "/repo/.worktrees/foss", "agent/foss"),
            Path.Combine(Path.GetTempPath(), "wrapup-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task Clean_and_green_merges_and_cleans_up()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit(); var tests = new FakeTests();
        var svc = new WrapUpService(git, tests);
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy("dotnet test", true, "main"), issues);
            Assert.Equal(WrapUpStatus.Merged, outcome.Status);
            Assert.True(git.Removed);
            Assert.True(git.BranchDeleted);
            Assert.Null(outcome.IssueId);
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }

    [Fact]
    public async Task Dirty_worktree_is_kept_and_not_merged()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit { Status = new GitStatus(true, 0, 0, false, System.Array.Empty<GitChange>()) };
        var svc = new WrapUpService(git, new FakeTests());
        var outcome = await svc.WrapUpAsync(req, GitPolicy.Default, issues);
        Assert.Equal(WrapUpStatus.KeptUncommitted, outcome.Status);
        Assert.False(git.Removed);
    }

    [Fact]
    public async Task Failing_tests_keep_worktree_and_file_an_issue()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit();
        var svc = new WrapUpService(git, new FakeTests { Pass = false });
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy("dotnet test", true, "main"), issues);
            Assert.Equal(WrapUpStatus.KeptTestsFailed, outcome.Status);
            Assert.False(git.Removed);
            Assert.NotNull(outcome.IssueId);
            Assert.Single(IssueStore.Read(issues));
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }

    [Fact]
    public async Task Merge_conflict_aborts_keeps_worktree_and_files_an_issue()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit { MergeOk = false };
        var svc = new WrapUpService(git, new FakeTests());
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy(null, true, "main"), issues);
            Assert.Equal(WrapUpStatus.KeptConflict, outcome.Status);
            Assert.True(git.MergeAborted);
            Assert.False(git.Removed);
            Assert.Single(IssueStore.Read(issues));
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }

    [Fact]
    public async Task Unreadable_status_keeps_worktree_and_does_not_merge()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit { StatusOk = false };
        var svc = new WrapUpService(git, new FakeTests());
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy("dotnet test", true, "main"), issues);
            Assert.Equal(WrapUpStatus.KeptUncommitted, outcome.Status);
            Assert.False(git.Removed);
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }

    [Fact]
    public async Task Merge_without_cleanup_when_policy_disables_removal()
    {
        var (req, issues) = Fixture();
        var git = new FakeGit();
        var svc = new WrapUpService(git, new FakeTests());
        try
        {
            var outcome = await svc.WrapUpAsync(req, new GitPolicy(null, false, "main"), issues);
            Assert.Equal(WrapUpStatus.Merged, outcome.Status);
            Assert.False(git.Removed);
            Assert.False(git.BranchDeleted);
        }
        finally { if (Directory.Exists(issues)) Directory.Delete(issues, true); }
    }
}
