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
    /// <summary>
    /// Polls until <paramref name="condition"/> holds or the timeout elapses. VM collections update on the
    /// (shared, headless) UI dispatcher, so under parallel test load they populate shortly AFTER the call
    /// that triggers them returns — a fixed delay races; this waits for the actual condition.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 10)
            await Task.Delay(10);
    }

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

            var outcome = await vm.SpawnChildAsync(new SpawnRequest(vm.Panes[0].Prefix, "iso-", "overlaps foss", ".", "p", Worktree: true));

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

                var outcome = await vm.SpawnChildAsync(new SpawnRequest(overviewPrefix, "newsub-", "owns X", ".", "You are newsub-.", false));

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
            var outcome = await vm.SpawnChildAsync(new SpawnRequest(vm.Panes[0].Prefix, "x-", "r", ".", "p", false));
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

    [Fact]
    public async Task SpawnProposed_is_owned_by_the_overview_keeping_the_authority_tree_single_rooted()
    {
        var repo = Path.Combine(Path.GetTempPath(), "hw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            // Overview mode: the first pane is the overview (root, no worktree).
            var cfg = ProjectScaffolder.Ensure(repo);
            var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher(),
                repoRoot: repo, overviewSystemPromptPath: cfg.SystemPromptPath);
            vm.AttachProject(cfg);
            // Panes populate via the (shared, headless) UI dispatcher, so under parallel load they aren't
            // guaranteed present the instant InitializeAsync/SpawnProposed return — wait for the condition
            // instead of a fixed delay (de-flake; mirrors the bus de-flake in ddb84cf).
            await WaitUntil(() => vm.Panes.Any(p => p.Prefix == "overview-"));
            var overview = vm.Panes.First(p => p.Prefix == "overview-");
            Assert.Equal("overview-", overview.Prefix);

            await vm.SpawnProposedAsync(new Styloagent.Core.Projects.ProposedAgent("hello-", "writes hello world", ".", "You are hello-."));

            await WaitUntil(() => vm.Panes.Any(p => p.Prefix == "hello-"));
            var child = vm.Panes.First(p => p.Prefix == "hello-");
            Assert.Equal("overview-", child.ParentPrefix);       // the overview OWNS it
            Assert.Equal(overview.Depth + 1, child.Depth);

            // The authority graph is a single-rooted tree (overview owns; no multiple roots).
            var violations = vm.LintAuthority();
            Assert.DoesNotContain(violations, v => v.Kind == "multiple-roots");

            // A spawned specialist can be dehydrated (parked): the Dehydrate command is enabled because it
            // now has a checkpoint path under the channel (previously empty → command disabled).
            Assert.True(child.DehydrateCommand.CanExecute(null), "spawned agent should be dehydratable");
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task SpawnProposed_with_worktree_true_creates_an_agent_branch()
    {
        var repo = Path.Combine(Path.GetTempPath(), "psp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var git = new RecordingGitService();
        try
        {
            var cfg = ProjectScaffolder.Ensure(repo);
            var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher(),
                gitService: git, repoRoot: repo, overviewSystemPromptPath: cfg.SystemPromptPath);
            vm.AttachProject(cfg);
            Assert.Equal("overview-", vm.Panes[0].Prefix);

            var outcome = await vm.SpawnProposedAsync(
                new ProposedAgent("iso-", "overlaps foss", ".", "You are iso-.", Worktree: true));

            Assert.True(outcome.Spawned);
            Assert.Equal("agent/iso", git.AddedBranch);
            var pane = vm.Panes.First(p => p.Prefix == "iso-");
            Assert.Equal("agent/iso", pane.WorktreeBranch);
            Assert.Equal("overview-", pane.ParentPrefix);   // still owned by the overview
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task Spawn_with_mission_doc_writes_it_into_the_worktree_and_points_the_prompt()
    {
        var repo = Path.Combine(Path.GetTempPath(), "spawnmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        var git = new RecordingGitService();
        try
        {
            var vm = await BuildOverviewVmAsync(repoRoot: repo, gitService: git);
            vm.AttachProject(ProjectConfig.For(repo));

            var outcome = await vm.SpawnChildAsync(new SpawnRequest(
                vm.Panes[0].Prefix, "bus-", "owns delivery", ".", "You are bus-.",
                Worktree: true, MissionDoc: "# Bus mission\nDeliver messages."));

            Assert.True(outcome.Spawned);
            var pane = vm.Panes.First(p => p.Prefix == "bus-");

            // The mission doc is written INTO the worktree at the conventional path.
            var missionPath = Path.Combine(pane.WorktreePath!, ".styloagent", "missions", "bus-.md");
            Assert.True(File.Exists(missionPath), $"mission doc should be inside the worktree at {missionPath}");
            Assert.Contains("Deliver messages.", File.ReadAllText(missionPath));

            // The injected launch prompt (persisted to launch-prompts) points at the mission doc and keeps
            // the caller's own launch text.
            var launchFile = Path.Combine(ProjectConfig.For(repo).LaunchPromptsDir, "bus-.md");
            Assert.True(File.Exists(launchFile));
            var prompt = File.ReadAllText(launchFile);
            Assert.Contains(".styloagent/missions/bus-.md", prompt);
            Assert.Contains("You are bus-.", prompt);
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task Spawn_does_not_clobber_a_preexisting_launch_prompt_doc()
    {
        var repo = Path.Combine(Path.GetTempPath(), "spawnclob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = await BuildOverviewVmAsync(repoRoot: repo);
            var cfg = ProjectConfig.For(repo);
            vm.AttachProject(cfg);

            // Someone (e.g. overview-) has already placed a doc at <prefix>.md before the spawn.
            Directory.CreateDirectory(cfg.LaunchPromptsDir);
            var preexisting = Path.Combine(cfg.LaunchPromptsDir, "docs-.md");
            const string authored = "# A hand-authored mission that must NOT be clobbered";
            File.WriteAllText(preexisting, authored);

            var outcome = await vm.SpawnChildAsync(new SpawnRequest(
                vm.Panes[0].Prefix, "docs-", "owns docs", ".", "You are docs- (short launch prompt).", Worktree: false));

            Assert.True(outcome.Spawned);
            // The pre-existing doc survived untouched.
            Assert.Equal(authored, File.ReadAllText(preexisting));
            // The spawn's launch prompt went to the reserved name instead of clobbering.
            var reserved = Path.Combine(cfg.LaunchPromptsDir, "docs-.launch.md");
            Assert.True(File.Exists(reserved), "launch prompt should fall back to the reserved <prefix>.launch.md");
            Assert.Contains("short launch prompt", File.ReadAllText(reserved));
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }
}
