using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetSpawnTests
{
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
