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
    private readonly IMessageInjector _injector;
    private readonly PendingInbox? _pending;

    // Recipient agentId -> messages waiting for that agent to go idle (NextPrompt while busy).
    // Only used on the injection FALLBACK path (non-MCP / hooks-not-wired recipients); connected
    // recipients defer via their own Stop hook instead of this queue.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<BusMessage>> _deferred = new();

    /// <summary>The active policy. Swappable at runtime (e.g. when a project is attached) without
    /// discarding already-deferred messages.</summary>
    public PriorityPolicy Policy { get; set; }

    /// <param name="pending">
    /// The MCP-native primary delivery store. When supplied, a message to a hook-connected recipient is
    /// surfaced through the recipient's own turn-boundary hooks (design
    /// <c>2026-07-13-mcp-native-delivery-design.md</c>) rather than typed into its PTY; injection stays the
    /// fallback for waking an already-idle session and for non-MCP recipients. When null, every delivery
    /// uses the injection fallback (the pre-MCP-native behaviour).
    /// </param>
    public MessageDeliveryService(PriorityPolicy policy, IMessageInjector injector, PendingInbox? pending = null)
    {
        Policy = policy;
        _injector = injector;
        _pending = pending;
    }

    /// <summary>
    /// Handle a new message addressed to <paramref name="recipientId"/>, whose current live state is
    /// <paramref name="recipientState"/>. Returns the action taken (useful for the HUD/tests).
    ///
    /// MCP-native primary path: a hook-connected recipient (any state other than <see cref="AgentHookState.Unknown"/>)
    /// has its note written to the <see cref="PendingInbox"/>, and its own turn-boundary hook surfaces it —
    /// no PTY typing. Injection is kept only for the two things a hook cannot do: <b>waking an already-idle
    /// session</b> (creating a new turn needs stdin) and <b>non-MCP / hooks-not-wired recipients</b>.
    /// </summary>
    public async Task<DeliveryAction> DeliverAsync(
        BusMessage message, string recipientId, AgentHookState recipientState, CancellationToken ct = default)
    {
        var mode = Policy.ModeFor(message.Priority);

        // An exited recipient can never receive anything.
        if (recipientState == AgentHookState.Exited)
            return DeliveryAction.None;

        bool connected = recipientState != AgentHookState.Unknown; // hooks live → MCP-native pull available
        bool pushing = mode is DeliveryMode.Interrupt or DeliveryMode.NextPrompt;

        if (connected && _pending is not null)
        {
            // Surfacing modes (low/info) → the .info file; shown at the next UserPromptSubmit, never forces.
            if (!pushing)
            {
                if (mode is DeliveryMode.Poll or DeliveryMode.Convenient or DeliveryMode.Informational)
                {
                    _pending.Enqueue(recipientId, MessageDelivery.FormatNudge(message), pushing: false);
                    return DeliveryAction.EnqueuePending;
                }
                return DeliveryAction.None;
            }

            // Pushing modes to a busy agent (Working / WaitingForHuman): a Stop boundary is coming — the
            // recipient's Stop hook force-continues into the message. This replaces the fragile mid-turn
            // ESC-break and the "defer-until-idle then type" path that caused the delivery bugs.
            if (recipientState != AgentHookState.Idle)
            {
                _pending.Enqueue(recipientId, MessageDelivery.FormatNudge(message), pushing: true);
                return DeliveryAction.EnqueuePending;
            }

            // Already idle: no hook will fire on its own → wake it via the injection fallback (the narrow,
            // least-fragile inject-at-a-prompt case; no ESC-break needed).
            await InjectAsync(recipientId, message, breakFirst: false, ct).ConfigureAwait(false);
            return DeliveryAction.Inject;
        }

        // Fallback: non-MCP / hooks-not-wired (Unknown), or no PendingInbox configured → the terminal injector.
        var action = MessageDelivery.Decide(mode, recipientState);
        switch (action)
        {
            case DeliveryAction.Inject:
                await InjectAsync(recipientId, message, breakFirst: false, ct).ConfigureAwait(false);
                break;

            case DeliveryAction.InjectWithBreak:
                await InjectAsync(recipientId, message, breakFirst: true, ct).ConfigureAwait(false);
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

    private Task InjectAsync(string recipientId, BusMessage message, bool breakFirst, CancellationToken ct)
        => _injector.InjectAsync(recipientId, MessageDelivery.FormatNudge(message), breakFirst, ct);

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
