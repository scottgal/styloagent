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

    /// <summary>Max ESC presses when breaking a turn — bounded so a turn that never idles can't hang us.</summary>
    public const int MaxBreakAttempts = 5;

    // Injecting the Enter the instant the text is typed drops it — Claude's TUI isn't ready — so the
    // message lingers unsent and needs a manual Enter. Production waits for the TUI to settle before
    // pressing Enter, presses once more as a safety net, and pauses between ESC presses so the turn has
    // time to actually die before we re-check idle. Tests leave these zero so the suite stays fast; the
    // app sets real values at startup (compare AgentSession.InjectSettleDelay/InjectEnterRetryDelay).
    public static TimeSpan BreakPollDelay { get; set; } = TimeSpan.Zero;
    public static TimeSpan SubmitSettleDelay { get; set; } = TimeSpan.Zero;
    public static TimeSpan SubmitRetryDelay { get; set; } = TimeSpan.Zero;

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
                await BreakTurnAsync(pty, ct).ConfigureAwait(false);
            await SubmitAsync(pty, text, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Breaks the recipient's current turn by pressing ESC repeatedly until it actually goes idle — a
    /// single ESC does NOT reliably kill Claude Code's turn. Re-checks <see cref="IPtySession.IsIdle"/>
    /// between presses (with <see cref="BreakPollDelay"/> to give the turn time to die) and is bounded by
    /// <see cref="MaxBreakAttempts"/> so a turn that never idles can't hang the injector.
    /// </summary>
    private static async Task BreakTurnAsync(IPtySession pty, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxBreakAttempts; attempt++)
        {
            if (pty.IsIdle) return;   // turn already broken — stop pressing ESC
            await pty.WriteAsync(Escape, ct).ConfigureAwait(false);
            if (BreakPollDelay > TimeSpan.Zero)
                await Task.Delay(BreakPollDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Types the message and submits it reliably. The Enter is a SEPARATE write from the text, with a
    /// settle delay between them (the TUI drops an Enter typed the instant the text arrives), plus a
    /// safety-net Enter after the TUI has certainly settled — so a delivered message is actually sent and
    /// never needs a manual Enter (compare the spawn-submit fix, AgentSession.InjectPromptAsync / 54ea63b).
    /// </summary>
    private static async Task SubmitAsync(IPtySession pty, string text, CancellationToken ct)
    {
        await pty.WriteAsync(text, ct).ConfigureAwait(false);
        if (SubmitSettleDelay > TimeSpan.Zero)
            await Task.Delay(SubmitSettleDelay, ct).ConfigureAwait(false);
        await pty.WriteAsync(Submit, ct).ConfigureAwait(false);
        if (SubmitRetryDelay > TimeSpan.Zero)
        {
            await Task.Delay(SubmitRetryDelay, ct).ConfigureAwait(false);
            await pty.WriteAsync(Submit, ct).ConfigureAwait(false);
        }
    }
}
