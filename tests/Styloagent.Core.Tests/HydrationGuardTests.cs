using System.Text.Json;
using Styloagent.Core.Hooks;

namespace Styloagent.Core.Tests;

/// <summary>
/// The compaction guard: on SessionStart source=compact|resume, an agent is re-fed its hydration text
/// (re-read your context doc, stay in scope, hand off/dehydrate rather than dilute) so it cannot compact
/// away its own identity.
/// </summary>
public class HydrationGuardTests
{
    [Fact]
    public void HydrationText_points_at_the_agents_own_doc_and_holds_it_to_scope()
    {
        var text = HydrationText.For("foss-", "/ch/saved-context/foss-context.md", "/ch/PROTOCOL.md", "/ch");

        Assert.Contains("foss-", text);
        Assert.Contains("/ch/saved-context/foss-context.md", text);   // re-read YOUR doc
        Assert.Contains("/ch/PROTOCOL.md", text);
        Assert.Contains("/ch/inbox/foss-*.md", text);
        // Scope-expansion guard is baked in.
        Assert.Contains("spawn_agent", text);
        Assert.Contains("dehydrate", text);
        Assert.Contains("scope", text);
    }

    [Fact]
    public void HydrationText_degrades_gracefully_without_paths()
    {
        var text = HydrationText.For("overview-", null, null, null);
        Assert.Contains("overview-", text);
        Assert.Contains("scope", text);   // still carries the identity + scope guard
    }

    [Fact]
    public void SessionStart_reinjects_hydration_only_on_compact_or_resume()
    {
        var json = HookSettings.BuildSettingsJson("foss-", "/tmp/hooks", "/tmp/hooks/foss.hydrate.json");
        using var doc = JsonDocument.Parse(json);
        var sessionStartCmd = HookCommand(doc, "SessionStart");

        Assert.Contains("\"compact\"", sessionStartCmd);
        Assert.Contains("\"resume\"", sessionStartCmd);
        Assert.Contains("additionalContext", sessionStartCmd);
        Assert.Contains("/tmp/hooks/foss.hydrate.json", sessionStartCmd);
        // Still drops the raw event for observation.
        Assert.Contains("/tmp/hooks/foss", sessionStartCmd);
    }

    [Fact]
    public void Non_SessionStart_events_stay_observe_only()
    {
        var json = HookSettings.BuildSettingsJson("foss-", "/tmp/hooks", "/tmp/hooks/foss.hydrate.json");
        using var doc = JsonDocument.Parse(json);
        var preToolCmd = HookCommand(doc, "PreToolUse");

        Assert.StartsWith("cat > ", preToolCmd);
        Assert.DoesNotContain("additionalContext", preToolCmd);
    }

    [Fact]
    public void Without_a_hydration_file_SessionStart_is_plain_observe()
    {
        var json = HookSettings.BuildSettingsJson("foss-", "/tmp/hooks");
        using var doc = JsonDocument.Parse(json);
        var sessionStartCmd = HookCommand(doc, "SessionStart");

        Assert.StartsWith("cat > ", sessionStartCmd);
        Assert.DoesNotContain("additionalContext", sessionStartCmd);
    }

    private static string HookCommand(JsonDocument doc, string ev)
        => doc.RootElement.GetProperty("hooks").GetProperty(ev)[0]
              .GetProperty("hooks")[0].GetProperty("command").GetString()!;

    [Fact]
    public void Scoped_permission_mode_allows_the_styloagent_mcp_tools_and_accepts_edits()
    {
        var json = HookSettings.BuildSettingsJson("foss-", "/tmp/hooks", null, FleetPermissionMode.Scoped);
        using var doc = JsonDocument.Parse(json);
        var perms = doc.RootElement.GetProperty("permissions");
        Assert.Equal("acceptEdits", perms.GetProperty("defaultMode").GetString());
        var allow = perms.GetProperty("allow").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("mcp__styloagent", allow);
        // Scoped needs no extra CLI flag.
        Assert.Empty(HookSettings.PermissionArgs(FleetPermissionMode.Scoped));
    }

    [Fact]
    public void Prompt_mode_adds_no_permissions_block_or_flag()
    {
        var json = HookSettings.BuildSettingsJson("foss-", "/tmp/hooks", null, FleetPermissionMode.Prompt);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("permissions", out _));
        Assert.Empty(HookSettings.PermissionArgs(FleetPermissionMode.Prompt));
    }

    [Fact]
    public void Bypass_mode_adds_the_skip_permissions_flag()
        => Assert.Contains("--dangerously-skip-permissions", HookSettings.PermissionArgs(FleetPermissionMode.Bypass));
}
