namespace Styloagent.Core.Channel;

/// <summary>
/// The concrete action a delivery executor takes for a message, once its <see cref="DeliveryMode"/>
/// has been resolved against the recipient's live state.
/// </summary>
public enum DeliveryAction
{
    /// <summary>Do nothing now (HUD-only modes, or the session is gone).</summary>
    None,

    /// <summary>Inject the nudge into the session now — it is already at a prompt.</summary>
    Inject,

    /// <summary>Send ESC to break the current turn, then inject the nudge (hard interrupt).</summary>
    InjectWithBreak,

    /// <summary>Hold the message until the recipient next goes idle, then inject.</summary>
    DeferUntilIdle,

    /// <summary>
    /// MCP-native primary path: the note was written to the recipient's <see cref="PendingInbox"/> and
    /// will be surfaced by the recipient's own turn-boundary hook (Stop force-continue for pushing modes,
    /// UserPromptSubmit additionalContext for surfacing modes) — no PTY injection. See design
    /// <c>2026-07-13-mcp-native-delivery-design.md</c>.
    /// </summary>
    EnqueuePending,
}
