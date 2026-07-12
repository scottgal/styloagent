using System.Text.Json;

namespace Styloagent.Core.Hooks;

/// <summary>
/// Parses the JSON a Claude Code hook command receives on stdin into a <see cref="HookEvent"/>.
/// Tolerant by design: a malformed or partial drop file must never crash the watcher.
/// </summary>
public static class HookEventParser
{
    /// <summary>
    /// Attempts to parse hook JSON for the given agent. Returns false (and null) on invalid JSON.
    /// </summary>
    public static bool TryParse(string json, string agentId, out HookEvent? evt)
    {
        evt = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            evt = new HookEvent(
                AgentId: agentId,
                EventName: Str(root, "hook_event_name") ?? string.Empty,
                NotificationType: Str(root, "notification_type"),
                Message: Str(root, "message"),
                SessionId: Str(root, "session_id"),
                Cwd: Str(root, "cwd"),
                ToolName: Str(root, "tool_name"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
