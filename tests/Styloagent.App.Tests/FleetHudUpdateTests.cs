using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class FleetHudUpdateTests
{
    [Fact]
    public async Task SpawnChild_raises_FleetCount_and_FleetHudText_PropertyChanged()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        MainWindowViewModel? vm = null;
        var proj = Path.Combine(Path.GetTempPath(), "hudupdateproj-" + Guid.NewGuid().ToString("N"));
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.AttachProject(ProjectScaffolder.Ensure(proj));

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null) raised.Add(e.PropertyName);
            };

            string parentPrefix = vm.Panes[0].Prefix;
            int countBefore = vm.FleetCount;
            var hudBefore = vm.FleetHudText;

            var outcome = await vm.SpawnChildAsync(new SpawnRequest(parentPrefix, "hud-", "r", ".", "p", false));

            Assert.True(outcome.Spawned);

            // (a) PropertyChanged raised for FleetCount and FleetHudText
            Assert.Contains("FleetCount", raised);
            Assert.Contains("FleetHudText", raised);

            // (b) FleetHudText now contains the incremented count
            int countAfter = vm.FleetCount;
            Assert.Equal(countBefore + 1, countAfter);
            Assert.Contains($"fleet {countAfter}/", vm.FleetHudText);
        }
        finally
        {
            vm?.Dispose();
            if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AttachProject_raises_FleetHudText_PropertyChanged()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        MainWindowViewModel? vm = null;
        var proj = Path.Combine(Path.GetTempPath(), "hudupdateproj2-" + Guid.NewGuid().ToString("N"));
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null) raised.Add(e.PropertyName);
            };

            vm.AttachProject(ProjectScaffolder.Ensure(proj));

            Assert.Contains("FleetHudText", raised);
            Assert.Contains("MaxFleet", raised);
            Assert.Contains("MaxDepth", raised);
        }
        finally
        {
            vm?.Dispose();
            if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
