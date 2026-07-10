using Styloagent.App.ViewModels;
using Styloagent.Core.Projects;

namespace Styloagent.App.Tests;

public class FleetWiringTests
{
    /// <summary>
    /// When InitializeAsync runs the overview path the fleet server is started inside it (before
    /// the AgentSession is built), so the overview spawn args already contain --mcp-config.
    /// </summary>
    [Fact]
    public async Task StartFleetServer_runs_and_overview_launches_with_mcp_config()
    {
        var proj = Path.Combine(Path.GetTempPath(), "wire-" + Guid.NewGuid().ToString("N"));
        var cfg = ProjectScaffolder.Ensure(proj);
        var launcher = new CapturingLauncher();
        MainWindowViewModel? vm = null;
        try
        {
            // The overview path inside InitializeAsync calls StartFleetServerAsync() before the
            // AgentSession args are assembled, so --mcp-config is present at spawn time.
            vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, launcher, new FakeWatcher(),
                repoRoot: cfg.Root, overviewSystemPromptPath: cfg.SystemPromptPath);

            // Idempotent: a second explicit call is a no-op.
            await vm.StartFleetServerAsync();

            Assert.True(vm.McpServerRunning);

            // McpArgsFor returns ["--mcp-config", "<json>"] when the server is running.
            var mcpArgs = vm.McpArgsFor("overview-");
            Assert.NotEmpty(mcpArgs);
            Assert.Equal("--mcp-config", mcpArgs[0]);

            // The captured overview spawn args must contain --mcp-config (proves ordering fix).
            Assert.Single(launcher.Options);
            var spawnArgs = launcher.Options[0].Args.ToList();
            Assert.Contains("--mcp-config", spawnArgs);
        }
        finally
        {
            vm?.Dispose();
            if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true);
        }
    }

    /// <summary>
    /// AttachProject reads fleet.yaml and populates FleetPolicy with MaxFleet / MaxDepth.
    /// </summary>
    [Fact]
    public async Task AttachProject_loads_the_fleet_policy()
    {
        var proj = Path.Combine(Path.GetTempPath(), "pol-" + Guid.NewGuid().ToString("N"));
        var cfg = ProjectScaffolder.Ensure(proj);
        File.WriteAllText(cfg.FleetPolicyPath, "maxFleet: 5\nmaxDepth: 2\n");
        MainWindowViewModel? vm = null;
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(cfg.ChannelRoot, new FakeLauncher(), new FakeWatcher());
            vm.AttachProject(cfg);

            Assert.Equal(5, vm.FleetPolicy.MaxFleet);
            Assert.Equal(2, vm.FleetPolicy.MaxDepth);
        }
        finally
        {
            vm?.Dispose();
            if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true);
        }
    }
}
