namespace Styloagent.Core.Channel;

/// <summary>
/// How the recipient's runtime nudges the agent about a message — the "interruption ladder".
/// Resolved per-project from a message's <see cref="MessagePriority"/> via
/// <see cref="Styloagent.Core.Projects.PriorityPolicy"/>.
/// </summary>
public enum DeliveryMode
{
    /// <summary>Send ESC to the live session to break the current turn, then inject immediately.</summary>
    Interrupt,

    /// <summary>Queue; inject when the agent next reaches idle (Notification idle_prompt).</summary>
    NextPrompt,

    /// <summary>Not pushed; the agent checks the channel on its own cadence.</summary>
    Poll,

    /// <summary>Not pushed; surfaced in the Bus HUD only — the agent reads it when convenient.</summary>
    Convenient,

    /// <summary>Never actioned or injected; shown as info only.</summary>
    Informational,
}
