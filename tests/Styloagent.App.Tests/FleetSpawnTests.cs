using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetSpawnTests
{
    private sealed class RecordingGitService : IGitService
    {
        public string? AddedBranch;
        public Task<GitResult<GitStatus>> GetStatusAsync(string worktreePath, CancellationToken ct = default)
            => Task.FromResult(GitResult<GitStatus>.Success(GitStatus.Clean));
        public Task<GitResult> AddWorktreeAsync(string repoRoot, string worktreePath, string newBranch, CancellationToken ct = default)
        { AddedBranch = newBranch; Directory.CreateDirectory(worktreePath); return Task.FromResult(GitResult.Success()); }
        public Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> MergeNoFfAsync(string repoRoot, string sourceBranch, string intoBranch, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
        public Task<GitResult> DeleteBranchAsync(string repoRoot, string branch, bool force, CancellationToken ct = default) => Task.FromResult(GitResult.Success());
    }

    /// <summary>
    /// Builds a VM the same way as the other FleetSpawnTests do but accepts an optional
    /// gitService and repoRoot (new params for Task 7). Existing tests call without args (defaults to null).
    /// </summary>
    private static async Task<MainWindowViewModel> BuildOverviewVmAsync(
        string? repoRoot = null,
        IGitService? gitService = null)
    {
        var channelRoot = MainWindowViewModelTests.MakeTwoAgentChannel();
        return await MainWindowViewModel.InitializeAsync(
            channelRoot,
            new FakeLauncher(),
            new FakeWatcher(),
            gitService: gitService,
            repoRoot: repoRoot);
    }

    [Fact]
    public async Task Spawn_with_worktree_creates_an_agent_branch()
    {
        var repo = Path.Combine(Path.GetTempPath(), "spawnwt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var git = new RecordingGitService();
        try
        {
            var vm = await BuildOverviewVmAsync(repoRoot: repo, gitService: git);
            vm.AttachProject(ProjectConfig.For(repo));

            var outcome = vm.SpawnChild(new SpawnRequest(vm.Panes[0].Prefix, "iso-", "overlaps foss", ".", "p", Worktree: true));

            Assert.True(outcome.Spawned);
            Assert.Equal("agent/iso", git.AddedBranch);
            var pane = vm.Panes.First(p => p.Prefix == "iso-");
            Assert.Equal("agent/iso", pane.WorktreeBranch);
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task SpawnChild_adds_a_parented_pane_at_depth_one()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();  // reuse existing helper
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            // Attach a project so child launch prompts have somewhere to go.
            var proj = Path.Combine(Path.GetTempPath(), "fleetproj-" + Guid.NewGuid().ToString("N"));
            vm.AttachProject(ProjectScaffolder.Ensure(proj));
            try
            {
                var overviewPrefix = vm.Panes[0].Prefix;   // first live agent acts as parent
                int before = vm.Panes.Count;

                var outcome = vm.SpawnChild(new SpawnRequest(overviewPrefix, "newsub-", "owns X", ".", "You are newsub-.", false));

                Assert.True(outcome.Spawned);
                Assert.Equal(before + 1, vm.Panes.Count);
                var child = vm.Panes.First(p => p.Prefix == "newsub-");
                Assert.Equal(overviewPrefix, child.ParentPrefix);
                Assert.Equal(vm.Panes[0].Depth + 1, child.Depth);
            }
            finally { if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SpawnChild_is_rejected_when_paused()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.PauseFleetCommand.Execute(null);
            var outcome = vm.SpawnChild(new SpawnRequest(vm.Panes[0].Prefix, "x-", "r", ".", "p", false));
            Assert.False(outcome.Spawned);
            Assert.Equal(RejectReason.Paused, outcome.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BuildFleetSnapshot_reflects_the_roster()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var snap = vm.BuildFleetSnapshot();
            Assert.Equal(vm.Panes.Count, snap.Members.Count);
            Assert.Equal(12, snap.MaxFleet);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
