using System.Collections.Concurrent;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Cross-cutting signal for which live PTY sessions currently have the OPERATOR composing a line in their
/// pane — from the first keystroke of a line until Enter/submit or Ctrl-C (ETX) / Ctrl-D (EOT). The
/// terminal view publishes it from its own compose state; <c>PtyMessageInjector</c> reads it to DEFER a
/// message-delivery nudge instead of typing into — and prematurely submitting — the operator's
/// half-finished line.
/// </summary>
/// <remarks>
/// Keyed by <see cref="IPtySession"/> so the injector (which resolves an agent id to its session) can ask
/// about the exact pane it is about to write into. Thread-safe: published on the UI thread (key/text
/// input) and read on the delivery thread. Best-effort — a stale read only mistimes a defer by one poll.
/// An entry exists ONLY while the operator is actively mid-line and is removed on submit and on pane
/// detach, so the map stays tiny and never pins a dead session.
/// </remarks>
public static class OperatorInputState
{
    private static readonly ConcurrentDictionary<IPtySession, byte> _composing = new();

    /// <summary>Records whether the operator is mid-line (composing) in the pane bound to <paramref name="session"/>.</summary>
    public static void SetComposing(IPtySession session, bool composing)
    {
        if (composing) _composing[session] = 0;
        else _composing.TryRemove(session, out _);
    }

    /// <summary>True when the operator is composing a line in the pane bound to <paramref name="session"/>.</summary>
    public static bool IsComposing(IPtySession? session)
        => session is not null && _composing.ContainsKey(session);

    /// <summary>Drops any recorded state for a session (on pane detach / session teardown).</summary>
    public static void Clear(IPtySession session) => _composing.TryRemove(session, out _);
}
