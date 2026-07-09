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
public sealed record HookEvent(
    string AgentId,
    string EventName,
    string? NotificationType,
    string? Message,
    string? SessionId,
    string? Cwd);
