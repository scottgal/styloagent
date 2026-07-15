using Styloagent.App.ViewModels;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class GitOpTimelineTests
{
    [Fact]
    public async Task LogGitBranchChange_writes_a_structured_switched_branch_timeline_op()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var cfg = ProjectScaffolder.Ensure(repo);
            var vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher(),
                repoRoot: repo, overviewSystemPromptPath: cfg.SystemPromptPath);
            vm.AttachProject(cfg);

            vm.LogGitBranchChange("fix/foo");
            Assert.Contains(vm.ReadTimeline(10), o => o.What == "switched branch · fix/foo");

            vm.LogGitBranchChange(null);   // detached HEAD
            Assert.Contains(vm.ReadTimeline(10), o => o.What == "detached HEAD");
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true); }
    }
}
