namespace Styloagent.Core.Channel;

/// <summary>
/// Injects a delivery nudge into a recipient agent's live session. The App/Terminal layer implements
/// this over the PTY (ESC to break a turn, then type + submit); Core stays platform-free and testable.
/// </summary>
public interface IMessageInjector
{
    /// <summary>
    /// Inject <paramref name="text"/> into <paramref name="agentId"/>'s session. When
    /// <paramref name="breakFirst"/> is true, send ESC to break the current turn before injecting.
    /// </summary>
    Task InjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default);
}
