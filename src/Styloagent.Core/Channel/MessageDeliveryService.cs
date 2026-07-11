using System.Collections.Concurrent;
using Styloagent.Core.Hooks;
using Styloagent.Core.Projects;

namespace Styloagent.Core.Channel;

/// <summary>
/// Applies a project's <see cref="PriorityPolicy"/> to incoming messages: resolves each message's
/// delivery mode, decides the action against the recipient's live hook state, and either injects
/// now (optionally breaking the turn) or defers until the recipient next goes idle.
///
/// Pure of any UI/PTY concern — it drives an <see cref="IMessageInjector"/>. Thread-safe.
/// </summary>
public sealed class MessageDeliveryService
{
    private readonly PriorityPolicy _policy;
    private readonly IMessageInjector _injector;

    // Recipient agentId -> messages waiting for that agent to go idle (NextPrompt while busy).
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BusMessage>> _deferred = new();

    public MessageDeliveryService(PriorityPolicy policy, IMessageInjector injector)
    {
        _policy = policy;
        _injector = injector;
    }

    /// <summary>
    /// Handle a new message addressed to <paramref name="recipientId"/>, whose current live state is
    /// <paramref name="recipientState"/>. Returns the action taken (useful for the HUD/tests).
    /// </summary>
    public async Task<DeliveryAction> DeliverAsync(
        BusMessage message, string recipientId, AgentHookState recipientState, CancellationToken ct = default)
    {
        var mode = _policy.ModeFor(message.Priority);
        var action = MessageDelivery.Decide(mode, recipientState);

        switch (action)
        {
            case DeliveryAction.Inject:
                await _injector.InjectAsync(recipientId, MessageDelivery.FormatNudge(message), breakFirst: false, ct)
                    .ConfigureAwait(false);
                break;

            case DeliveryAction.InjectWithBreak:
                await _injector.InjectAsync(recipientId, MessageDelivery.FormatNudge(message), breakFirst: true, ct)
                    .ConfigureAwait(false);
                break;

            case DeliveryAction.DeferUntilIdle:
                _deferred.GetOrAdd(recipientId, _ => new ConcurrentQueue<BusMessage>()).Enqueue(message);
                break;

            case DeliveryAction.None:
            default:
                break;
        }

        return action;
    }

    /// <summary>
    /// Notify the service that <paramref name="agentId"/> transitioned to <paramref name="newState"/>.
    /// When it goes idle, any messages deferred for it are injected in arrival order.
    /// </summary>
    public async Task OnRecipientStateChangedAsync(string agentId, AgentHookState newState, CancellationToken ct = default)
    {
        if (newState != AgentHookState.Idle) return;
        if (!_deferred.TryRemove(agentId, out var queue)) return;

        while (queue.TryDequeue(out var message))
        {
            await _injector.InjectAsync(agentId, MessageDelivery.FormatNudge(message), breakFirst: false, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Count of messages currently deferred for <paramref name="agentId"/> (for the HUD/tests).</summary>
    public int DeferredCount(string agentId) =>
        _deferred.TryGetValue(agentId, out var q) ? q.Count : 0;
}
