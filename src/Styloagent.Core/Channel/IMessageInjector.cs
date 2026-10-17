namespace Styloagent.Core.Channel;

/// <summary>
/// Injects a delivery nudge into a recipient agent's live session. The App/Terminal layer implements
/// this over the PTY (ESC to break a turn, then type + submit); Core stays platform-free and testable.
/// </summary>
public enum InjectionResult { Accepted, Unavailable, Deferred }

public interface IMessageInjector
{
    /// <summary>True when a native API accepted this input. False selects the legacy hook/PTY path.</summary>
    Task<bool> TryDeliverNativeAsync(string agentId, BusMessage message, DeliveryMode mode, CancellationToken ct = default)
        => Task.FromResult(false);
    /// <summary>Transport admission only; a PTY cannot prove model consumption.</summary>
    async Task<InjectionResult> TryInjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default)
    {
        await InjectAsync(agentId, text, breakFirst, ct).ConfigureAwait(false);
        return InjectionResult.Accepted;
    }

    /// <summary>
    /// Inject <paramref name="text"/> into <paramref name="agentId"/>'s session. When
    /// <paramref name="breakFirst"/> is true, send ESC to break the current turn before injecting.
    /// </summary>
    Task InjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default);
}
