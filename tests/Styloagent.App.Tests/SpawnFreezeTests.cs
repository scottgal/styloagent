using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Regression for the cockpit-freeze class: spawning a worktree agent ran
/// <c>AddWorktreeAsync(...).GetAwaiter().GetResult()</c> ON THE UI THREAD (FleetController marshals spawn
/// there, and the roster's Spawn button runs on it), so <c>git worktree add</c> (which does a checkout)
/// blocked the UI thread. Spawn must be genuinely async so the UI thread is released while the worktree
/// add runs.
/// </summary>
public class SpawnFreezeTests
{
    /// <summary>
    /// Fake git whose worktree add DELAYS before succeeding — models the checkout a real <c>git worktree
    /// add</c> does. A non-blocking <c>SpawnChildAsync</c> returns an INCOMPLETE task while this is in
    /// flight; a version that blocks on it only returns once it finishes (the freeze).
    /// </summary>
    private sealed class DelayingWorktreeGitService : IGitService
    {
        private readonly int _delayMs;
        public string? AddedBranch;
        public DelayingWorktreeGitService(int delayMs) => _delayMs = delayMs;

        public Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(GitStatus.Clean));

        public async Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default)
        {
            AddedBranch = newBranch;
            await Task.Delay(_delayMs, ct);
            Directory.CreateDirectory(worktreePath);
            return GitResult.Success();
        }

        public Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    [Fact]
    public async Task SpawnChildAsync_does_not_block_the_caller_on_worktree_add()
    {
        var repo = Path.Combine(Path.GetTempPath(), "spawnfreeze-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var git = new DelayingWorktreeGitService(delayMs: 300);
        try
        {
            var channelRoot = MainWindowViewModelTests.MakeTwoAgentChannel();
            var vm = await MainWindowViewModel.InitializeAsync(
                channelRoot, new FakeLauncher(), new FakeWatcher(), gitService: git, repoRoot: repo);
            vm.AttachProject(ProjectConfig.For(repo));

            // Act: kick off a worktree spawn. The slow worktree add (checkout) is in flight (300ms delay).
            var task = vm.SpawnChildAsync(new SpawnRequest(vm.Panes[0].Prefix, "iso-", "r", ".", "p", Worktree: true));

            // Assert: the call returned WITHOUT waiting for the worktree add — the task is still running.
            // A blocking implementation would only return after the add completed (task already done).
            Assert.False(task.IsCompleted,
                "SpawnChildAsync blocked the calling thread on git worktree add — this is the cockpit-freeze bug.");

            var outcome = await task;
            Assert.True(outcome.Spawned);
            Assert.Equal("agent/iso", git.AddedBranch);
            var pane = vm.Panes.First(p => p.Prefix == "iso-");
            Assert.Equal("agent/iso", pane.WorktreeBranch);
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }
}
