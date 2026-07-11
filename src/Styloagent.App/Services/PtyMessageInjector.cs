using Styloagent.Core.Channel;
using Styloagent.Core.Sessions;

namespace Styloagent.App.Services;

/// <summary>
/// PTY-backed <see cref="IMessageInjector"/>: resolves an agent id to its live session and types a
/// delivery nudge into it. When breaking a turn, sends ESC first. Writes are serialized because
/// <see cref="IPtySession.WriteAsync"/> is not safe for concurrent callers.
/// </summary>
public sealed class PtyMessageInjector : IMessageInjector
{
    private const string Escape = "\x1b";   // ESC — breaks Claude Code's current turn
    private const string Submit = "\r";      // Enter — submits the typed nudge

    private readonly Func<string, IPtySession?> _resolve;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <param name="resolve">Maps an agent id (pane prefix) to its current live PTY, or null if none.</param>
    public PtyMessageInjector(Func<string, IPtySession?> resolve) => _resolve = resolve;

    public async Task InjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default)
    {
        var pty = _resolve(agentId);
        if (pty is null) return;   // no live session — nothing to inject into

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (breakFirst)
                await pty.WriteAsync(Escape, ct).ConfigureAwait(false);
            await pty.WriteAsync(text + Submit, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
