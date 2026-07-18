using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Styloagent.App.Services;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.App.Tests;

public class PtyMessageInjectorTests
{
    private const string Esc = "\x1b";
    private const string Cr = "\r";

    /// <summary>
    /// Fake PTY whose turn is "killed" (becomes idle) only after it has received a set number of ESC
    /// presses — models Claude Code, where a single ESC does NOT reliably break the current turn.
    /// Records every write so tests can assert the exact ESC-repeat + submit sequence.
    /// </summary>
    private sealed class BreakingPty : IPtySession
    {
        private readonly int _escapesToIdle;
        private int _escapes;

        public BreakingPty(int escapesToIdle) => _escapesToIdle = escapesToIdle;

        public List<string> Writes { get; } = new();
#pragma warning disable CS0067
        public event Action<string>? Output;
        public event Action? Exited;
#pragma warning restore CS0067
        public bool IsIdle => _escapes >= _escapesToIdle;

        public ValueTask WriteAsync(string text, CancellationToken ct = default)
        {
            Writes.Add(text);
            if (text == Esc) _escapes++;
            return ValueTask.CompletedTask;
        }

        public void Resize(int cols, int rows) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Inject_without_break_types_text_then_submits_as_separate_writes()
    {
        var pty = new BreakingPty(escapesToIdle: 0);
        var injector = new PtyMessageInjector(id => id == "beta" ? pty : null);

        await injector.InjectAsync("beta", "hello", breakFirst: false);

        // The Enter is a SEPARATE write from the text (so a settle delay can sit between them in
        // production) — not the old single "hello\r" that got dropped and needed a manual Enter.
        Assert.Equal(new[] { "hello", Cr }, pty.Writes);
    }

    [Fact]
    public async Task Inject_with_break_sends_escape_repeatedly_until_idle_then_submits()
    {
        // Turn only dies after the 3rd ESC — one ESC is not enough (the bug this fixes).
        var pty = new BreakingPty(escapesToIdle: 3);
        var injector = new PtyMessageInjector(_ => pty);

        await injector.InjectAsync("beta", "hello", breakFirst: true);

        Assert.Equal(new[] { Esc, Esc, Esc, "hello", Cr }, pty.Writes);
    }

    [Fact]
    public async Task Inject_with_break_is_bounded_when_turn_never_goes_idle()
    {
        // Turn never idles: the injector must NOT loop forever — it stops after MaxBreakAttempts
        // ESC presses and still delivers the message.
        var pty = new BreakingPty(escapesToIdle: int.MaxValue);
        var injector = new PtyMessageInjector(_ => pty);

        await injector.InjectAsync("beta", "hello", breakFirst: true);

        int escapes = pty.Writes.Count(w => w == Esc);
        Assert.Equal(PtyMessageInjector.MaxBreakAttempts, escapes);
        // Still submits after giving up on the break.
        Assert.Equal("hello", pty.Writes[^2]);
        Assert.Equal(Cr, pty.Writes[^1]);
    }

    [Fact]
    public async Task Inject_presses_enter_again_as_safety_net_when_retry_delay_is_set()
    {
        var prev = PtyMessageInjector.SubmitRetryDelay;
        PtyMessageInjector.SubmitRetryDelay = TimeSpan.FromMilliseconds(1);
        try
        {
            var pty = new BreakingPty(escapesToIdle: 0);
            var injector = new PtyMessageInjector(_ => pty);

            await injector.InjectAsync("beta", "hello", breakFirst: false);

            // text, submit, then a safety-net submit once the TUI has certainly settled.
            Assert.Equal(new[] { "hello", Cr, Cr }, pty.Writes);
        }
        finally
        {
            PtyMessageInjector.SubmitRetryDelay = prev;
        }
    }

    [Fact]
    public async Task Inject_flattens_embedded_newlines_so_the_only_submit_is_the_trailing_enter()
    {
        // A multi-line nudge must not submit early or drop the TUI into multi-line input: the injector
        // collapses embedded CR/LF to spaces so the ONE separate Enter is the only submit.
        var pty = new BreakingPty(escapesToIdle: 0);
        var injector = new PtyMessageInjector(_ => pty);

        await injector.InjectAsync("beta", "line one\nline two\r\nline three", breakFirst: false);

        Assert.Equal(new[] { "line one line two line three", Cr }, pty.Writes);
    }

    [Fact]
    public async Task No_live_session_is_a_no_op()
    {
        var injector = new PtyMessageInjector(_ => null);
        // Must not throw when there is nothing to inject into.
        await injector.InjectAsync("ghost", "hello", breakFirst: true);
    }

    // ── Compose gate: don't clobber the operator's typing ────────────────────────

    [Fact]
    public async Task Inject_defers_while_operator_is_composing_then_flushes_on_submit()
    {
        var prevTimeout = PtyMessageInjector.ComposeDeferTimeout;
        var prevPoll = PtyMessageInjector.ComposePollDelay;
        PtyMessageInjector.ComposeDeferTimeout = TimeSpan.FromSeconds(5);
        PtyMessageInjector.ComposePollDelay = TimeSpan.FromMilliseconds(5);
        var pty = new BreakingPty(escapesToIdle: 0);
        try
        {
            // Operator is mid-line in this pane.
            OperatorInputState.SetComposing(pty, true);
            var injector = new PtyMessageInjector(_ => pty);

            var inject = injector.InjectAsync("beta", "hello", breakFirst: false);

            // While the operator is composing, the delivery must NOT type anything into their line.
            await Task.Delay(60);
            Assert.False(inject.IsCompleted, "delivery should be deferred while the operator is composing");
            Assert.Empty(pty.Writes);

            // Operator submits — the compose window closes; the delivery flushes now.
            OperatorInputState.SetComposing(pty, false);
            await inject;

            Assert.Equal(new[] { "hello", Cr }, pty.Writes);
        }
        finally
        {
            OperatorInputState.Clear(pty);
            PtyMessageInjector.ComposeDeferTimeout = prevTimeout;
            PtyMessageInjector.ComposePollDelay = prevPoll;
        }
    }

    [Fact]
    public async Task Inject_is_bounded_and_delivers_when_operator_never_submits()
    {
        var prevTimeout = PtyMessageInjector.ComposeDeferTimeout;
        var prevPoll = PtyMessageInjector.ComposePollDelay;
        PtyMessageInjector.ComposeDeferTimeout = TimeSpan.FromMilliseconds(100);
        PtyMessageInjector.ComposePollDelay = TimeSpan.FromMilliseconds(5);
        var pty = new BreakingPty(escapesToIdle: 0);
        try
        {
            // Operator starts a line and never submits — the gate must not starve delivery forever.
            OperatorInputState.SetComposing(pty, true);
            var injector = new PtyMessageInjector(_ => pty);

            await injector.InjectAsync("beta", "hello", breakFirst: false);

            Assert.Equal(new[] { "hello", Cr }, pty.Writes);
        }
        finally
        {
            OperatorInputState.Clear(pty);
            PtyMessageInjector.ComposeDeferTimeout = prevTimeout;
            PtyMessageInjector.ComposePollDelay = prevPoll;
        }
    }

    [Fact]
    public async Task Inject_does_not_defer_when_operator_is_not_composing()
    {
        var prevTimeout = PtyMessageInjector.ComposeDeferTimeout;
        PtyMessageInjector.ComposeDeferTimeout = TimeSpan.FromSeconds(30);   // long — proves we DON'T wait
        var pty = new BreakingPty(escapesToIdle: 0);
        try
        {
            // No compose flag for this pane → the fast path injects immediately, no wait.
            var injector = new PtyMessageInjector(_ => pty);

            var inject = injector.InjectAsync("beta", "hello", breakFirst: false);
            await inject.WaitAsync(TimeSpan.FromSeconds(2));   // must complete well within the 30s timeout

            Assert.Equal(new[] { "hello", Cr }, pty.Writes);
        }
        finally
        {
            OperatorInputState.Clear(pty);
            PtyMessageInjector.ComposeDeferTimeout = prevTimeout;
        }
    }
}
