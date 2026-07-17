using Styloagent.Core.Channel;

namespace Styloagent.Core.Attention;

/// <summary>
/// The per-message "picked up" projection the bus viewer reads to render the middle "being-worked-on"
/// lifecycle pill (issue <c>signal-bus-viewer-fadecollapse-completed-message</c>). A bus message is
/// <b>picked up</b> once it has been delivered into the recipient's MCP-native pending queue AND that note
/// is no longer waiting there — i.e. the recipient's turn-boundary hook (or an in-session
/// <c>check_inbox</c>) has drained it, so the recipient has begun handling it.
///
/// Keyed by the message's own <c>(FilePath, RoutingPrefix)</c>: <c>RoutingPrefix</c> is the recipient prefix
/// the note was delivered under (the same token delivery routes and keys by), so a viewer row looks its own
/// status up directly with no extra plumbing. cockpit- ANDs this with "unreplied" to distinguish
/// <c>WAITING</c> (unreplied, not picked up) from <c>BEING WORKED ON</c> (unreplied, picked up); a
/// reply/archive supersedes both as <c>DONE</c>.
///
/// Derivation, not a stored flag: the drain is a POSIX-<c>sh</c> hook with no C# in the loop, so pickup is
/// observed from delivery state (the delivered ledger AND the message's absence from the live pending files)
/// rather than written at drain time. Degrades safely — a lost pending store reads as "picked up" (the
/// message is still durable in the channel); a lost delivered ledger reads as "not yet" (WAITING), the
/// conservative UI. A null <see cref="PendingInbox"/> (delivery not MCP-wired) makes every message
/// not-picked-up, so the viewer simply shows WAITING/DONE as it does today.
/// </summary>
public sealed class PickupProjection
{
    private readonly PendingInbox? _pending;

    public PickupProjection(PendingInbox? pending) => _pending = pending;

    /// <summary>
    /// True once the message at <paramref name="filePath"/>, delivered under recipient prefix
    /// <paramref name="routingPrefix"/>, has been picked up. False while still pending, never delivered, or
    /// when no pending store is wired.
    /// </summary>
    public bool IsPickedUp(string filePath, string routingPrefix) =>
        _pending is not null && _pending.PickedUp(routingPrefix, filePath);
}
