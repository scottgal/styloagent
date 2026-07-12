namespace Styloagent.Core.Hooks;

/// <summary>
/// A single Claude Code hook event, parsed from the JSON a hook command receives on stdin.
/// <see cref="AgentId"/> is not carried in the payload — it comes from the drop-file name that
/// Styloagent assigned when it built the per-agent hook command (§4.4).
/// </summary>
/// <param name="AgentId">The Styloagent agent this event belongs to (from the file name).</param>
/// <param name="EventName">The hook event name, e.g. <c>PreToolUse</c>, <c>Notification</c>.</param>
/// <param name="NotificationType">For <c>Notification</c> events: e.g. <c>permission_prompt</c>, <c>idle_prompt</c>.</param>
/// <param name="Message">Human-readable message, when present (what the agent is waiting on).</param>
/// <param name="SessionId">Claude session id, for correlation/debugging.</param>
/// <param name="Cwd">The agent's working directory at the time of the event.</param>
/// <param name="ToolName">For <c>PreToolUse</c>/<c>PostToolUse</c>: the tool being run (e.g. <c>Read</c>, <c>Bash</c>) — drives the activity detail.</param>
/// <param name="ToolTarget">What the tool acts on — a file path (Read/Edit/Write), a command (Bash), or a pattern (Grep/Glob) from <c>tool_input</c>.</param>
/// <param name="ToolOld">For an <c>Edit</c>: the <c>old_string</c> being replaced (drives the diff view).</param>
/// <param name="ToolNew">For an <c>Edit</c>: the <c>new_string</c> replacing it.</param>
public sealed record HookEvent(
    string AgentId,
    string EventName,
    string? NotificationType,
    string? Message,
    string? SessionId,
    string? Cwd,
    string? ToolName = null,
    string? ToolTarget = null,
    string? ToolOld = null,
    string? ToolNew = null);
