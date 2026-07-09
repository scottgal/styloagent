namespace Styloagent.Core.Hooks;

/// <summary>
/// Pure state transition for an agent's <see cref="AgentHookState"/> given a hook event (§4.4).
///
/// Design notes:
/// - <c>Stop</c> fires on EVERY turn, not at task completion, so it is NOT a reliable idle
///   signal — it never changes state. True idle comes only from <c>Notification(idle_prompt)</c>.
/// - Benign notifications (auth success, elicitation dismissed, …) leave the state unchanged so
///   they can't produce a false "needs-you" alarm.
/// </summary>
public static class HookStateMachine
{
    public static AgentHookState Next(AgentHookState current, HookEvent e) => e.EventName switch
    {
        "SessionStart"     => AgentHookState.Working,
        "SessionEnd"       => AgentHookState.Exited,
        "UserPromptSubmit" => AgentHookState.Working,
        "PreToolUse"       => AgentHookState.Working,
        "PostToolUse"      => AgentHookState.Working,
        "Notification"     => FromNotification(current, e.NotificationType),
        "Stop"             => current, // fires every turn — not a reliable idle signal
        _                  => current,
    };

    private static AgentHookState FromNotification(AgentHookState current, string? notificationType)
        => notificationType switch
        {
            "idle_prompt"        => AgentHookState.Idle,
            "permission_prompt"  => AgentHookState.WaitingForHuman,
            "agent_needs_input"  => AgentHookState.WaitingForHuman,
            "elicitation_dialog" => AgentHookState.WaitingForHuman,
            _                    => current, // auth_success, elicitation_complete, … — no change
        };
}
