namespace Styloagent.Core.Channel;

/// <summary>
/// The semantic priority a sender stamps on a bus message via the <c>**Priority:**</c> header.
/// How a level actually interrupts the recipient is resolved per-project by
/// <see cref="Styloagent.Core.Projects.PriorityPolicy"/> → <see cref="DeliveryMode"/>.
/// Absent/unrecognized header defaults to <see cref="Normal"/>.
/// </summary>
public enum MessagePriority
{
    /// <summary>Break in as soon as allowed (default policy: Interrupt).</summary>
    Urgent,

    /// <summary>The default. Deliver at the next natural boundary (default policy: NextPrompt).</summary>
    Normal,

    /// <summary>No hurry (default policy: Convenient).</summary>
    Low,

    /// <summary>FYI only, never actioned (default policy: Informational).</summary>
    Info,
}
