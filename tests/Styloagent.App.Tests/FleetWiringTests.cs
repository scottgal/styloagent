using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
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
                repoRoot: cfg.Root, overviewSystemPromptPath: cfg.SystemPromptPath,
                defaultAgentRuntime: AgentRuntimeKind.Claude);

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

    [Fact]
    public async Task Codex_overview_launches_with_codex_mcp_config()
    {
        var proj = Path.Combine(Path.GetTempPath(), "wire-codex-" + Guid.NewGuid().ToString("N"));
        var cfg = ProjectScaffolder.Ensure(proj);
        var launcher = new CapturingLauncher();
        MainWindowViewModel? vm = null;
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, launcher, new FakeWatcher(),
                repoRoot: cfg.Root,
                overviewSystemPromptPath: cfg.SystemPromptPath,
                defaultAgentRuntime: AgentRuntimeKind.Codex);

            Assert.True(vm.McpServerRunning);
            Assert.Single(launcher.Options);
            var spawnArgs = launcher.Options[0].Args.ToList();
            Assert.Equal("codex", launcher.Options[0].Command);
            Assert.DoesNotContain("--mcp-config", spawnArgs);
            Assert.Contains(spawnArgs, a => a.Contains("mcp_servers.styloagent.url", StringComparison.Ordinal));
            Assert.Contains(spawnArgs, a => a.Contains("\"X-Styloagent-Agent\"=\"overview-\"", StringComparison.Ordinal));
            Assert.Contains(spawnArgs, a => a.StartsWith("developer_instructions=", StringComparison.Ordinal)
                                            && a.Contains("overview / architect", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            vm?.Dispose();
            if (Directory.Exists(proj)) Directory.Delete(proj, recursive: true);
        }
    }

    /// <summary>
    /// Kilo overview launches as `kilo run` with the pro DeepSeek default; the MCP server rides the
    /// per-agent KILO_CONFIG_CONTENT env (no --mcp-config/--settings CLI args), and the observation
    /// plugin is installed into the repo's .kilo/plugins/.
    /// </summary>
    [Fact]
    public async Task Kilo_overview_launches_headless_with_config_content_env()
    {
        var proj = Path.Combine(Path.GetTempPath(), "wire-kilo-" + Guid.NewGuid().ToString("N"));
        var cfg = ProjectScaffolder.Ensure(proj);
        var launcher = new CapturingLauncher();
        MainWindowViewModel? vm = null;
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, launcher, new FakeWatcher(),
                repoRoot: cfg.Root,
                overviewSystemPromptPath: cfg.SystemPromptPath,
                defaultAgentRuntime: AgentRuntimeKind.Kilo);

            Assert.True(vm.McpServerRunning);
            Assert.Single(launcher.Options);
            var spawn = launcher.Options[0];
            Assert.Equal("kilo", spawn.Command);
            // Interactive TUI launch: --model only (prompt typed via PTY; approvals from config).
            Assert.Equal("--model", spawn.Args[0]);
            Assert.Contains(spawn.Args, a => a == Styloagent.Core.Sessions.AgentRuntimeProfile.KiloDefaultModelId);
            Assert.DoesNotContain("run", spawn.Args);
            Assert.DoesNotContain("--auto", spawn.Args);
            Assert.DoesNotContain("--mcp-config", spawn.Args);
            Assert.DoesNotContain("--settings", spawn.Args);

            // MCP identity rides the per-agent config content env, carrying THIS agent's prefix.
            Assert.NotNull(spawn.Env);
            Assert.True(spawn.Env.ContainsKey("KILO_CONFIG_CONTENT"));
            var content = spawn.Env["KILO_CONFIG_CONTENT"];
            Assert.Contains("\"styloagent\"", content);
            Assert.Contains("overview-", content);
            Assert.Equal("overview-", spawn.Env["STYLOAGENT_AGENT_ID"]);

            // The fleet-observation plugin was installed for the worktree/repo root.
            Assert.True(File.Exists(Styloagent.Core.Hooks.KiloHooksPlugin.PathFor(cfg.Root)));
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
