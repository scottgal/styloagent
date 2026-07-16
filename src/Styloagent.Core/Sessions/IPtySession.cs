namespace Styloagent.Core.Sessions;

public interface IPtySession : IAsyncDisposable
{
    ValueTask WriteAsync(string text, CancellationToken ct = default);
    event Action<string>? Output;
    event Action? Exited;
    bool IsIdle { get; }

    /// <summary>
    /// The OS process id of the child spawned by this PTY (e.g. for a Force-kill feature). Returns 0 when no
    /// process id is available (e.g. a fake/test session, or the process is gone). Must never throw.
    /// </summary>
    /// <remarks>
    /// Default-implemented as 0 so test doubles and non-process sessions need no change; the real PTY
    /// (<c>PortaPtySession</c>) overrides it to surface the child's OS pid from Porta.Pty.
    /// </remarks>
    int ProcessId => 0;

    void Resize(int cols, int rows);
}
