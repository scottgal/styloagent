using Styloagent.Core.Sessions;

namespace Styloagent.UITests;

/// <summary>
/// Deterministic test double for <see cref="IPtySession"/>.
/// - Call <see cref="FireOutput"/> to raise Output (synchronously, on the caller's thread).
/// - Inspect <see cref="Writes"/> to verify what was forwarded to WriteAsync.
/// - Inspect <see cref="LastResize"/> to verify Resize calls.
/// </summary>
public sealed class FakePtySession : IPtySession
{
    private readonly List<string> _writes = new();
    private Action<string>? _output;
    private string? _backlog;

    /// <summary>
    /// Mirrors <c>PortaPtySession.Output</c>: on subscribe, any seeded <see cref="SeedBacklog"/> is replayed
    /// to the new handler synchronously FIRST (so a late-attaching view rebuilds VT state from history), then
    /// live <see cref="FireOutput"/> follows. This is the seam that reproduces a pane RE-ATTACH.
    /// </summary>
    public event Action<string>? Output
    {
        add { if (value is not null && !string.IsNullOrEmpty(_backlog)) value(_backlog); _output += value; }
        remove { _output -= value; }
    }

    public event Action? Exited;

    /// <summary>
    /// Seeds the replay backlog that <see cref="Output"/> hands to each new subscriber — i.e. the output the
    /// child already produced BEFORE this view attached (the real session buffers this to bridge the
    /// spawn→attach gap and every re-attach). Use it to drive the replay path a live <see cref="FireOutput"/>
    /// cannot reach.
    /// </summary>
    public void SeedBacklog(string text) => _backlog = text;

    /// <summary>
    /// Whether the fake PTY looks idle. Defaults true (no output flowing) like a settled session; set it
    /// false to simulate a MID-TURN session (output streaming) so the injector's ESC turn-break actually
    /// fires — the real PortaPtySession.IsIdle is false while Claude is producing output.
    /// </summary>
    public bool IsIdle { get; set; } = true;

    public IReadOnlyList<string> Writes => _writes.AsReadOnly();

    /// <summary>Clears the recorded writes list. Useful for isolating test assertions.</summary>
    public void ClearWrites() => _writes.Clear();

    public (int Cols, int Rows)? LastResize { get; private set; }

    /// <summary>
    /// Raises <see cref="Output"/> with <paramref name="text"/>, simulating LIVE PTY output (after subscribe).
    /// </summary>
    public void FireOutput(string text) => _output?.Invoke(text);

    /// <summary>Raises <see cref="Exited"/>.</summary>
    public void FireExited() => Exited?.Invoke();

    /// <summary>Optional callback invoked synchronously on every WriteAsync call.</summary>
    public Action<string>? OnWrite { get; set; }

    public ValueTask WriteAsync(string text, CancellationToken ct = default)
    {
        _writes.Add(text);
        OnWrite?.Invoke(text);
        return ValueTask.CompletedTask;
    }

    public void Resize(int cols, int rows) => LastResize = (cols, rows);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
