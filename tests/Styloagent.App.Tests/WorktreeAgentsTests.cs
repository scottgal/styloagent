using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Xunit;

namespace Styloagent.App.Tests;

public class WorktreeAgentsTests
{
    private sealed class FakeGitReader : IGitReader
    {
        private readonly IReadOnlyList<GitWorktree> _wts;
        public FakeGitReader(params GitWorktree[] wts) => _wts = wts;
        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repoRoot, CancellationToken ct = default)
            => Task.FromResult(_wts);
    }

    [Fact]
    public async Task Agents_come_from_detected_worktrees_and_launch_in_them()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"wtrepo-{Guid.NewGuid():N}");
        var wtA = Path.Combine(repo, "alpha");
        var wtB = Path.Combine(repo, "beta");
        Directory.CreateDirectory(wtA);
        Directory.CreateDirectory(wtB);
        try
        {
            var reader = new FakeGitReader(
                new GitWorktree(wtA, "alpha", "aaa"),
                new GitWorktree(wtB, "beta", "bbb"));
            var launcher = new FakeLauncher();

            var vm = await MainWindowViewModel.InitializeAsync(
                "/tmp/no-such-channel", launcher, new FakeWatcher(), reader, repo);

            // First agent = first worktree, not a channel prefix.
            Assert.NotNull(vm.Pane);
            Assert.Equal("alpha", vm.Pane!.DisplayName);

            // It launches claude in that worktree's directory (deterministic awaited spawn).
            await vm.Pane.SpawnCommand.ExecuteAsync(null);
            Assert.Contains(launcher.Options, o => o.WorkingDirectory == wtA);

            // The dock has one document; AddAgent opens the second worktree.
            Assert.Equal(1, vm.DocumentDock!.VisibleDockables!.Count);
            vm.AddAgentCommand.Execute(null);
            Assert.Equal(2, vm.DocumentDock.VisibleDockables!.Count);
        }
        finally { Directory.Delete(repo, true); }
    }

    [Fact]
    public async Task No_worktrees_falls_back_to_a_single_agent_in_the_repo_root()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"emptyrepo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        try
        {
            var reader = new FakeGitReader();     // no worktrees
            var launcher = new FakeLauncher();

            var vm = await MainWindowViewModel.InitializeAsync(
                "/tmp/no-such-channel", launcher, new FakeWatcher(), reader, repo);

            Assert.NotNull(vm.Pane);
            await vm.Pane!.SpawnCommand.ExecuteAsync(null);
            Assert.Contains(launcher.Options, o => o.WorkingDirectory == repo);
        }
        finally { Directory.Delete(repo, true); }
    }
}
