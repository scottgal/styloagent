using Styloagent.Core.Hooks;

namespace Styloagent.Core.Channel;

/// <summary>
/// Pure decision layer for message delivery: given a resolved <see cref="DeliveryMode"/> and the
/// recipient's live <see cref="AgentHookState"/>, decide the concrete <see cref="DeliveryAction"/>,
/// and format the nudge text that gets injected.
/// </summary>
public static class MessageDelivery
{
    /// <summary>
    /// Resolve the action for a message. Modes that never push (Poll/Convenient/Informational) are
    /// always <see cref="DeliveryAction.None"/>. An <see cref="AgentHookState.Exited"/> recipient can
    /// never receive an injection, so pushing modes also yield <see cref="DeliveryAction.None"/>.
    /// </summary>
    public static DeliveryAction Decide(DeliveryMode mode, AgentHookState recipientState)
    {
        if (recipientState == AgentHookState.Exited)
            return DeliveryAction.None;

        return mode switch
        {
            // Break in as soon as allowed. If the agent is mid-turn, ESC to break first; if it is
            // already at a prompt (Idle / blocked / not-yet-known) just inject.
            DeliveryMode.Interrupt => recipientState == AgentHookState.Working
                ? DeliveryAction.InjectWithBreak
                : DeliveryAction.Inject,

            // Deliver at the next natural boundary: inject if already idle, otherwise wait for idle.
            DeliveryMode.NextPrompt => recipientState == AgentHookState.Idle
                ? DeliveryAction.Inject
                : DeliveryAction.DeferUntilIdle,

            // Poll / Convenient / Informational never push.
            _ => DeliveryAction.None,
        };
    }

    /// <summary>The one-line nudge injected into the recipient's session for a delivered message.</summary>
    public static string FormatNudge(BusMessage message)
    {
        string from = string.IsNullOrWhiteSpace(message.From) ? "" : $" from {message.From.Trim()}";
        return $"[bus] {message.Priority.ToString().ToLowerInvariant()} message \"{message.Slug}\"{from} — read it: {message.FilePath}";
    }
}
