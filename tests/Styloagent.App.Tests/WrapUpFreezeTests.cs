using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Regression for the cockpit-freeze class found alongside the terminal livelock: the wrap-up path ran
/// <c>WrapUpAsync(...).GetAwaiter().GetResult()</c> ON THE UI THREAD (FleetController marshals WrapUp there),
/// so the whole app froze for the entire test → merge → cleanup — seconds to minutes. Wrap-up must be
/// genuinely async so the UI thread is released while the git/test I/O runs.
/// </summary>
public class WrapUpFreezeTests
{
    /// <summary>
    /// Fake git whose status read DELAYS before returning dirty — models the slow git I/O a real wrap-up
    /// does. A non-blocking <c>WrapUpAsync</c> returns an INCOMPLETE task while this is in flight; a version
    /// that blocks on it only returns once it finishes (the freeze). Dirty → wrap-up short-circuits at
    /// "uncommitted changes", keeping the fake surface minimal (no merge/cleanup calls exercised).
    /// </summary>
    private sealed class DelayingGitService : IGitService
    {
        private readonly int _delayMs;
        public DelayingGitService(int delayMs) => _delayMs = delayMs;

        public async Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
        {
            await Task.Delay(_delayMs, ct);
            return GitResult<GitStatus>.Success(new GitStatus(IsDirty: true, 0, 0, false, System.Array.Empty<GitChange>()));
        }

        public Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default)
        { Directory.CreateDirectory(worktreePath); return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    [Fact]
    public async Task WrapUpAsync_does_not_block_the_caller_on_git_io()
    {
        var repo = Path.Combine(Path.GetTempPath(), "wrapfreeze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var git = new DelayingGitService(delayMs: 300);
        try
        {
            var channelRoot = MainWindowViewModelTests.MakeTwoAgentChannel();
            var vm = await MainWindowViewModel.InitializeAsync(
                channelRoot, new FakeLauncher(), new FakeWatcher(), gitService: git, repoRoot: repo);
            vm.AttachProject(ProjectConfig.For(repo));

            // Give an agent a worktree so wrap-up has something to act on.
            var spawn = await vm.SpawnChildAsync(new SpawnRequest(vm.Panes[0].Prefix, "wrap-", "r", ".", "p", Worktree: true));
            Assert.True(spawn.Spawned);

            // Act: kick off wrap-up. The slow git status read is in flight (300ms delay).
            var task = vm.WrapUpAsync("wrap-");

            // Assert: the call returned WITHOUT waiting for the git I/O — the task is still running.
            // A blocking implementation would only return after the read completed (task already done).
            Assert.False(task.IsCompleted,
                "WrapUpAsync blocked the calling thread on git I/O — this is the cockpit-freeze bug.");

            var outcome = await task;
            Assert.Equal(WrapUpStatus.KeptUncommitted, outcome.Status);   // dirty worktree → kept, not merged
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }
}
