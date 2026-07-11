using Styloagent.Core.Hooks;

namespace Styloagent.Core.Channel;

/// <summary>A live agent's identity + current hook state, as seen by delivery.</summary>
public readonly record struct AgentPresence(string Prefix, AgentHookState State);

/// <summary>
/// Watches a channel for <em>new</em> deliverable messages and pushes each to its recipient(s) via a
/// <see cref="MessageDeliveryService"/>. Owns the "already seen" set so a message is delivered once.
///
/// Pure of UI/PTY concerns: it reads the channel with <see cref="ChannelProjection"/>, asks a caller
/// snapshot for the live agents, routes with <see cref="MessageRouting"/>, and delegates the actual
/// push (inject / defer) to the service. Thread-safe; pumps are serialized.
/// </summary>
public sealed class ChannelDeliveryCoordinator
{
    private readonly string _channelRoot;
    private readonly IReadOnlyCollection<string> _knownPrefixes;
    private readonly MessageDeliveryService _delivery;
    private readonly Func<IReadOnlyList<AgentPresence>> _liveAgents;
    private readonly ChannelProjection _projection = new();

    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChannelDeliveryCoordinator(
        string channelRoot,
        IReadOnlyCollection<string> knownPrefixes,
        MessageDeliveryService delivery,
        Func<IReadOnlyList<AgentPresence>> liveAgents)
    {
        _channelRoot = channelRoot;
        _knownPrefixes = knownPrefixes;
        _delivery = delivery;
        _liveAgents = liveAgents;
    }

    /// <summary>
    /// Mark all currently-present messages as already seen, so startup does not deliver the channel's
    /// backlog — only messages that arrive afterwards are pushed. Call once before wiring reloads.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var m in await AllMessagesAsync(ct).ConfigureAwait(false))
                _seen.Add(m.FilePath);
        }
        catch { /* tolerant: a bad read just means nothing is seeded */ }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Read the channel and deliver every not-yet-seen, non-archived message to its live recipient(s).
    /// Returns the number of (message,recipient) deliveries attempted this pump (for the HUD/tests).
    /// </summary>
    public async Task<int> PumpAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var agents = _liveAgents();
            var prefixes = agents.Select(a => a.Prefix).ToList();
            int delivered = 0;

            foreach (var msg in await AllMessagesAsync(ct).ConfigureAwait(false))
            {
                if (msg.State == BusMessageState.Archived) continue;
                if (!_seen.Add(msg.FilePath)) continue;   // already handled

                foreach (var recipientId in MessageRouting.RecipientsFor(msg, prefixes))
                {
                    var state = agents.First(a => a.Prefix == recipientId).State;
                    await _delivery.DeliverAsync(msg, recipientId, state, ct).ConfigureAwait(false);
                    delivered++;
                }
            }

            return delivered;
        }
        catch { return 0; }   // tolerant: never let a bad channel read throw into the reload path
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<BusMessage>> AllMessagesAsync(CancellationToken ct)
    {
        var threads = await _projection.ReadAsync(_channelRoot, _knownPrefixes, ct).ConfigureAwait(false);
        return threads.SelectMany(t => t.Messages).ToList();
    }
}
