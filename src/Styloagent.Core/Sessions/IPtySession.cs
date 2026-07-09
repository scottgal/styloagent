namespace Styloagent.Core.Sessions;

public interface IPtySession : IAsyncDisposable
{
    ValueTask WriteAsync(string text, CancellationToken ct = default);
    event Action<string>? Output;
    event Action? Exited;
    bool IsIdle { get; }
    void Resize(int cols, int rows);
}
