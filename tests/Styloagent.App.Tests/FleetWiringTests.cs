using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;
using System.Text.Json;

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
            Assert.DoesNotContain("--ax-screen-reader", spawnArgs);
            Assert.Equal("1", launcher.Options[0].Env!["CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN"]);
            Assert.DoesNotContain(spawnArgs, a => a.StartsWith("mcp_servers.", StringComparison.Ordinal));
            var mcpIndex = spawnArgs.IndexOf("--mcp-config");
            using var mcpConfig = JsonDocument.Parse(spawnArgs[mcpIndex + 1]);
            var token = launcher.Options[0].Env![Mcp.McpConfig.TokenEnvironmentVariable];
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.DoesNotContain(token, spawnArgs);
            Assert.Equal(JsonValueKind.Array, mcpConfig.RootElement
                .GetProperty("mcpServers").GetProperty("chrome-devtools").GetProperty("args").ValueKind);
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
            Assert.Contains("--no-alt-screen", spawnArgs);
            Assert.Contains("--dangerously-bypass-hook-trust", spawnArgs);
            Assert.Contains(spawnArgs, a => a.Contains("mcp_servers.styloagent.url", StringComparison.Ordinal));
            Assert.Contains(spawnArgs, a => a.StartsWith("mcp_servers.chrome-devtools.args=[", StringComparison.Ordinal)
                                            && a.EndsWith(']'));
            Assert.Contains(spawnArgs, a => a.Contains("\"X-Styloagent-Agent\"=\"overview-\"", StringComparison.Ordinal));
            var token = launcher.Options[0].Env![Mcp.McpConfig.TokenEnvironmentVariable];
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.DoesNotContain(token, spawnArgs);
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
    /// THE acceptance test: the stylobot-commercial-style overview must launch as Claude Code routed
    /// through DeepSeek (not real-Anthropic Opus). The Opus TIER on the claude-deepseek runtime must
    /// resolve to --model deepseek-flash (the stable V4.1 Flash alias), with the
    /// DeepSeek base URL + key env applied, and never pass the literal 'opus' model or the Anthropic
    /// base URL.
    /// </summary>
    [Fact]
    public async Task ClaudeDeepSeek_overview_launches_routed_to_deepseek_not_opus()
    {
        var proj = Path.Combine(Path.GetTempPath(), "wire-cds-" + Guid.NewGuid().ToString("N"));
        var cfg = ProjectScaffolder.Ensure(proj);
        var launcher = new CapturingLauncher();
        MainWindowViewModel? vm = null;
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(
                cfg.ChannelRoot, launcher, new FakeWatcher(),
                repoRoot: cfg.Root,
                overviewSystemPromptPath: cfg.SystemPromptPath,
                defaultAgentRuntime: AgentRuntimeKind.ClaudeDeepSeek);

            Assert.True(vm.McpServerRunning);
            var spawn = Assert.Single(launcher.Options);
            Assert.Equal("claude", spawn.Command);
            Assert.Contains("--mcp-config", spawn.Args);
            Assert.DoesNotContain("--ax-screen-reader", spawn.Args);
            Assert.Equal("1", spawn.Env!["CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN"]);
            Assert.Equal("1000000", spawn.Env!["CLAUDE_CODE_MAX_CONTEXT_TOKENS"]);
            Assert.DoesNotContain("--dangerously-bypass-hook-trust", spawn.Args);
            Assert.DoesNotContain(spawn.Args, a => a.StartsWith("mcp_servers.", StringComparison.Ordinal));

            // Opus tier on claude-deepseek -> DeepSeek V4.1 Flash, NEVER the literal claude 'opus' model.
            var modelIdx = spawn.Args.ToList().IndexOf("--model");
            Assert.True(modelIdx >= 0, "claude must receive an explicit --model");
            Assert.Equal("deepseek-flash", spawn.Args[modelIdx + 1]);
            Assert.DoesNotContain("--model", spawn.Args.Skip(modelIdx + 1));
            Assert.DoesNotContain("opus", spawn.Args, StringComparer.Ordinal);

            // The DeepSeek routing env must be applied; the Anthropic base URL must NOT be.
            Assert.NotNull(spawn.Env);
            Assert.Equal("https://api.deepseek.com/anthropic", spawn.Env["ANTHROPIC_BASE_URL"]);
            Assert.NotEmpty(spawn.Env["ANTHROPIC_AUTH_TOKEN"]);
            Assert.False(spawn.Env.ContainsKey("ANTHROPIC_BASE_URL") && spawn.Env["ANTHROPIC_BASE_URL"]!.Contains("anthropic.com"),
                "must not route claude to the real Anthropic API");

            // The spawned claude CLI's own catalog has never heard of "deepseek-flash" (it's a custom id
            // behind a custom ANTHROPIC_BASE_URL). Without a modelPicker/behavesAs row in --settings it
            // logs "[claude-code:unrecognized_model]" and assumes a 200k context window instead of the
            // model's real 1M one.
            var settingsIdx = spawn.Args.ToList().IndexOf("--settings");
            Assert.True(settingsIdx >= 0, "claude-deepseek must receive an explicit --settings blob");
            using var settingsDoc = System.Text.Json.JsonDocument.Parse(spawn.Args[settingsIdx + 1]);
            var picker = settingsDoc.RootElement.GetProperty("modelPicker");
            var row = Assert.Single(picker.GetProperty("options").EnumerateArray());
            Assert.Equal("deepseek-flash", row.GetProperty("model").GetString());
            Assert.Equal("sonnet", row.GetProperty("behavesAs").GetString());
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
