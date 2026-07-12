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
                ToolName: Str(root, "tool_name"),
                ToolTarget: ToolTarget(root),
                ToolOld: ToolInput(root, "old_string"),
                ToolNew: ToolInput(root, "new_string"));
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

    /// <summary>
    /// Extracts what a tool acts on from <c>tool_input</c>: a file path (Read/Edit/Write/Notebook),
    /// else a command (Bash), else a search pattern (Grep/Glob). Null when none apply.
    /// </summary>
    private static string? ToolTarget(JsonElement root)
    {
        if (!root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;
        return Str(input, "file_path")
            ?? Str(input, "notebook_path")
            ?? Str(input, "command")
            ?? Str(input, "pattern");
    }

    /// <summary>Reads a string field from <c>tool_input</c> (e.g. <c>old_string</c>/<c>new_string</c>).</summary>
    private static string? ToolInput(JsonElement root, string name)
        => root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object
            ? Str(input, name)
            : null;
}
