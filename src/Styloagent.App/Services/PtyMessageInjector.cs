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

    // Compose gate: a delivery must NOT type a nudge into a line the OPERATOR is mid-way through composing
    // in the target pane, or it clobbers their input and prematurely submits the half-typed line. When the
    // operator is composing (TerminalControl publishes it via OperatorInputState), the injector WAITS for
    // them to submit — bounded by ComposeDeferTimeout so a never-submitted line can't starve delivery.
    // Non-zero by default so the gate is live in production without startup wiring (App.axaml.cs may retune
    // it alongside the other delays); tests override both.
    /// <summary>How long a delivery waits for the operator to finish composing before injecting anyway.</summary>
    public static TimeSpan ComposeDeferTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Poll interval while waiting for the operator's compose window to close.</summary>
    public static TimeSpan ComposePollDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    private readonly Func<string, IPtySession?> _resolve;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <param name="resolve">Maps an agent id (pane prefix) to its current live PTY, or null if none.</param>
    public PtyMessageInjector(Func<string, IPtySession?> resolve) => _resolve = resolve;

    public async Task InjectAsync(string agentId, string text, bool breakFirst, CancellationToken ct = default)
    {
        var pty = _resolve(agentId);
        if (pty is null) return;   // no live session — nothing to inject into

        // Don't clobber the operator mid-keystroke: if they're composing a line in this pane, wait for them
        // to submit it (or a bounded timeout) before typing the nudge. Same compose window TerminalControl
        // tracks to gate device-report echoes — a delivery is the analogous "unwanted bytes into the typed
        // line" hazard, so it defers on the same signal.
        await DeferWhileOperatorComposingAsync(pty, ct).ConfigureAwait(false);

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
    /// Waits out the operator's compose window in the target pane so a delivery nudge doesn't type into — and
    /// prematurely submit — their half-finished line. Returns immediately when the operator isn't composing
    /// there or the gate is disabled; polls until the window closes (operator submits) and, on
    /// <see cref="ComposeDeferTimeout"/>, returns anyway so a never-submitted line can't starve delivery.
    /// </summary>
    private static async Task DeferWhileOperatorComposingAsync(IPtySession pty, CancellationToken ct)
    {
        if (ComposeDeferTimeout <= TimeSpan.Zero) return;          // gate disabled
        if (!OperatorInputState.IsComposing(pty)) return;         // fast path — operator isn't typing here

        var poll = ComposePollDelay > TimeSpan.Zero ? ComposePollDelay : TimeSpan.FromMilliseconds(10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ComposeDeferTimeout);
        try
        {
            while (OperatorInputState.IsComposing(pty))
                await Task.Delay(poll, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Operator never submitted within the timeout — inject anyway rather than starve delivery.
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
        // Always press ESC at least once: an urgent break must attempt to interrupt even when the agent
        // LOOKS idle — IsIdle is just "no output for ~250ms", which a mid-turn agent that's quietly thinking
        // trips. So press, THEN check idle; keep pressing until the turn actually dies, bounded.
        for (int attempt = 0; attempt < MaxBreakAttempts; attempt++)
        {
            await pty.WriteAsync(Escape, ct).ConfigureAwait(false);
            if (BreakPollDelay > TimeSpan.Zero)
                await Task.Delay(BreakPollDelay, ct).ConfigureAwait(false);
            if (pty.IsIdle) return;   // turn broken — stop pressing ESC
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
        // Flatten any embedded newline: a CR/LF inside the nudge would either submit it early or drop
        // Claude Code's TUI into multi-line input, so the SEPARATE Enter below would add a line instead of
        // sending. The nudge is one logical line — collapse stray newlines to spaces so the trailing Enter
        // is the ONLY submit. (The message body itself is never injected; only a one-line pointer is.)
        string line = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        await pty.WriteAsync(line, ct).ConfigureAwait(false);
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
