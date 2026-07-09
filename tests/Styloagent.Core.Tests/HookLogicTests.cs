using System.Text.Json;
using Styloagent.Core.Hooks;
using Xunit;

namespace Styloagent.Core.Tests;

public class HookLogicTests
{
    // ── HookEventParser ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_extracts_notification_fields()
    {
        const string json = """
            {"session_id":"s1","cwd":"/repo","hook_event_name":"Notification",
             "notification_type":"permission_prompt","message":"Allow Bash?"}
            """;

        Assert.True(HookEventParser.TryParse(json, "web", out var e));
        Assert.NotNull(e);
        Assert.Equal("web", e!.AgentId);
        Assert.Equal("Notification", e.EventName);
        Assert.Equal("permission_prompt", e.NotificationType);
        Assert.Equal("Allow Bash?", e.Message);
        Assert.Equal("s1", e.SessionId);
        Assert.Equal("/repo", e.Cwd);
    }

    [Fact]
    public void Parse_tolerates_missing_optional_fields()
    {
        Assert.True(HookEventParser.TryParse("""{"hook_event_name":"Stop"}""", "a", out var e));
        Assert.Equal("Stop", e!.EventName);
        Assert.Null(e.NotificationType);
        Assert.Null(e.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")] // valid JSON but not an object
    public void Parse_returns_false_on_bad_input(string bad)
    {
        Assert.False(HookEventParser.TryParse(bad, "a", out var e));
        Assert.Null(e);
    }

    // ── HookStateMachine ────────────────────────────────────────────────────

    private static HookEvent Ev(string name, string? notif = null)
        => new("a", name, notif, null, null, null);

    [Theory]
    [InlineData("UserPromptSubmit")]
    [InlineData("PreToolUse")]
    [InlineData("PostToolUse")]
    [InlineData("SessionStart")]
    public void Working_events_set_working(string name)
        => Assert.Equal(AgentHookState.Working, HookStateMachine.Next(AgentHookState.Idle, Ev(name)));

    [Fact]
    public void Permission_notification_sets_waiting_for_human()
        => Assert.Equal(AgentHookState.WaitingForHuman,
            HookStateMachine.Next(AgentHookState.Working, Ev("Notification", "permission_prompt")));

    [Theory]
    [InlineData("agent_needs_input")]
    [InlineData("elicitation_dialog")]
    public void Needs_input_notifications_set_waiting(string notif)
        => Assert.Equal(AgentHookState.WaitingForHuman,
            HookStateMachine.Next(AgentHookState.Working, Ev("Notification", notif)));

    [Fact]
    public void Idle_notification_sets_idle()
        => Assert.Equal(AgentHookState.Idle,
            HookStateMachine.Next(AgentHookState.Working, Ev("Notification", "idle_prompt")));

    [Fact]
    public void Benign_notification_leaves_state_unchanged()
        => Assert.Equal(AgentHookState.Working,
            HookStateMachine.Next(AgentHookState.Working, Ev("Notification", "auth_success")));

    [Fact]
    public void Stop_does_not_change_state_because_it_fires_every_turn()
        => Assert.Equal(AgentHookState.Working,
            HookStateMachine.Next(AgentHookState.Working, Ev("Stop")));

    [Fact]
    public void SessionEnd_sets_exited()
        => Assert.Equal(AgentHookState.Exited,
            HookStateMachine.Next(AgentHookState.Working, Ev("SessionEnd")));

    // ── HookSettings ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("web", "web")]
    [InlineData("wba-atom/llm", "wba-atom-llm")]
    [InlineData("a b__c", "a-b--c")]
    [InlineData("", "agent")]
    public void SanitizeAgentId_produces_safe_token(string input, string expected)
        => Assert.Equal(expected, HookSettings.SanitizeAgentId(input));

    [Fact]
    public void AgentId_round_trips_through_the_file_name()
    {
        string id = HookSettings.SanitizeAgentId("wba-atom/llm");
        string fileName = $"{id}__1A2B3C4D-0000.json";
        Assert.Equal(id, HookSettings.AgentIdFromFileName(fileName));
    }

    [Fact]
    public void AgentIdFromFileName_returns_null_for_non_matching_names()
        => Assert.Null(HookSettings.AgentIdFromFileName("random.json"));

    [Fact]
    public void BuildSettingsJson_is_valid_json_with_all_observed_events()
    {
        string json = HookSettings.BuildSettingsJson("web", "/tmp/hooks");

        using var doc = JsonDocument.Parse(json); // must be valid JSON
        var hooks = doc.RootElement.GetProperty("hooks");

        foreach (string ev in new[]
                 { "SessionStart", "SessionEnd", "UserPromptSubmit",
                   "PreToolUse", "PostToolUse", "Notification", "Stop" })
        {
            Assert.True(hooks.TryGetProperty(ev, out var arr), $"missing event {ev}");
            var entry = arr[0];
            var hook0 = entry.GetProperty("hooks")[0];
            Assert.Equal("command", hook0.GetProperty("type").GetString());
            string cmd = hook0.GetProperty("command").GetString()!;
            Assert.Contains("/tmp/hooks/web__", cmd);
            Assert.Contains("uuidgen", cmd);
        }
    }

    [Fact]
    public void BuildSettingsArgs_prefixes_with_settings_flag()
    {
        var args = HookSettings.BuildSettingsArgs("web", "/tmp/hooks");
        Assert.Equal("--settings", args[0]);
        using var doc = JsonDocument.Parse(args[1]); // second arg is the JSON blob
        Assert.True(doc.RootElement.TryGetProperty("hooks", out _));
    }
}
