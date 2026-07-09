namespace Styloagent.Core.Hooks;

/// <summary>
/// Live state of an agent derived from its Claude Code hook stream (§4.4).
/// This is distinct from <see cref="Model.SessionState"/> (the PTY lifecycle) — it reflects
/// what the agent is *doing* right now, so the roster can surface "who needs me" at a glance.
/// </summary>
public enum AgentHookState
{
    /// <summary>No hook events seen yet (just spawned, or hooks not wired).</summary>
    Unknown,

    /// <summary>Actively processing — a prompt was submitted or a tool is running.</summary>
    Working,

    /// <summary>Finished a turn and waiting for the next prompt (Notification: idle).</summary>
    Idle,

    /// <summary>⚠ Blocked on a human: a permission prompt, needs-input, or an MCP form.</summary>
    WaitingForHuman,

    /// <summary>The session ended (SessionEnd).</summary>
    Exited,
}
