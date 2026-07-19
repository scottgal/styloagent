using Styloagent.Core.Hooks;

namespace Styloagent.Core.Tests;

public class CodexHookSettingsTests
{
    [Fact]
    public void BuildConfigArgs_emits_codex_hook_config_for_observed_events()
    {
        var args = CodexHookSettings.BuildConfigArgs("agent/one-", "/tmp/stylo hooks");

        Assert.Contains("--dangerously-bypass-hook-trust", args);
        Assert.Contains("--config", args);
        Assert.Contains(args, a => a.StartsWith("hooks.SessionStart=", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("hooks.PreToolUse=", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("hooks.PermissionRequest=", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("hooks.Stop=", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("/tmp/stylo hooks/agent-one-__", StringComparison.Ordinal));
        Assert.Contains(args, a => a.Contains("cat > \\", StringComparison.Ordinal));
        Assert.DoesNotContain("--settings", args);
    }

    [Fact]
    public void BuildConfigArgs_wires_hydration_delivery_and_ownership_gate()
    {
        var args = CodexHookSettings.BuildConfigArgs(
            "foss-", "/tmp/hooks", "/tmp/hooks/foss.hydrate.json",
            "'dotnet' 'Styloagent.App.dll'", "/repo", "foss-");
        var joined = string.Join("\n", args);

        Assert.Contains("additionalContext", joined);
        Assert.Contains("SessionStart", joined);
        Assert.Contains("UserPromptSubmit", joined);
        Assert.Contains("decision\\\":\\\"block", joined);
        Assert.Contains(OwnershipGateCli.GateModeFlag, joined);
        Assert.Contains("--caller 'foss-'", joined);
        Assert.Contains("--root '/repo'", joined);
    }
}
