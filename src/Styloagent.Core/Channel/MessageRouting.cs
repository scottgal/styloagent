namespace Styloagent.Core.Channel;

/// <summary>
/// Pure resolver from a bus message to the agent(s) that should be nudged about it. Replies are not
/// delivered as interrupts (the reply lands in the thread the sender is already watching); broadcasts
/// (<c>all-</c>) go to every live agent; a plain inbox/follow-up message goes to the one agent whose
/// prefix it is addressed to.
/// </summary>
public static class MessageRouting
{
    /// <summary>The routing prefix that addresses every agent.</summary>
    public const string BroadcastPrefix = "all-";

    /// <summary>
    /// Recipients for <paramref name="message"/> among the currently-live <paramref name="availablePrefixes"/>.
    /// Empty when the message is a reply, or when its addressee is not live.
    /// </summary>
    public static IReadOnlyList<string> RecipientsFor(
        BusMessage message, IEnumerable<string> availablePrefixes)
    {
        var live = availablePrefixes as IReadOnlyCollection<string> ?? availablePrefixes.ToList();

        // Replies flow back into the sender's thread — they don't interrupt the addressee.
        if (message.Kind is BusMessageKind.Reply or BusMessageKind.BroadcastReply)
            return Array.Empty<string>();

        // Broadcast → everyone live (a sender broadcasting to itself is harmless; kept simple).
        if (message.Kind == BusMessageKind.Broadcast ||
            message.RoutingPrefix.Equals(BroadcastPrefix, StringComparison.OrdinalIgnoreCase))
            return live.ToList();

        // Addressed message → the single matching live agent, if any.
        return live
            .Where(p => p.Equals(message.RoutingPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
