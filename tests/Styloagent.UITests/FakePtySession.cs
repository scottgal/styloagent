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

    public event Action<string>? Output;
    public event Action? Exited;

    public bool IsIdle => true;

    public IReadOnlyList<string> Writes => _writes.AsReadOnly();

    public (int Cols, int Rows)? LastResize { get; private set; }

    /// <summary>
    /// Raises <see cref="Output"/> with <paramref name="text"/>, simulating PTY output.
    /// </summary>
    public void FireOutput(string text) => Output?.Invoke(text);

    /// <summary>Raises <see cref="Exited"/>.</summary>
    public void FireExited() => Exited?.Invoke();

    public ValueTask WriteAsync(string text, CancellationToken ct = default)
    {
        _writes.Add(text);
        return ValueTask.CompletedTask;
    }

    public void Resize(int cols, int rows) => LastResize = (cols, rows);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
