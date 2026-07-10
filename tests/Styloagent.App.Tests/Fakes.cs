using Styloagent.Core.Abstractions;
using Styloagent.Core.Sessions;

namespace Styloagent.App.Tests;

/// <summary>Minimal fake PTY session for VM tests.</summary>
internal sealed class FakePty : IPtySession
{
    public List<string> Writes { get; } = new();
    public bool Disposed { get; private set; }
#pragma warning disable CS0067
    public event Action<string>? Output;
    public event Action? Exited;
#pragma warning restore CS0067
    public bool IsIdle => true;
    public void Resize(int cols, int rows) { }
    public ValueTask WriteAsync(string text, CancellationToken ct = default) { Writes.Add(text); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

/// <summary>Minimal fake PTY launcher.</summary>
internal sealed class FakeLauncher : IPtyLauncher
{
    public List<FakePty> Spawned { get; } = new();
    public List<PtySpawnOptions> Options { get; } = new();

    public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
    {
        Options.Add(o);
        var p = new FakePty();
        Spawned.Add(p);
        return Task.FromResult<IPtySession>(p);
    }
}

/// <summary>Fake file watcher whose return value is configurable.</summary>
internal sealed class FakeWatcher : IFileWatcher
{
    public bool WillChange { get; set; } = true;

    public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult(WillChange);
}

/// <summary>
/// Capturing PTY launcher: records every <see cref="PtySpawnOptions"/> passed to
/// <see cref="SpawnAsync"/> so tests can assert on the args threaded into each launch.
/// </summary>
internal sealed class CapturingLauncher : IPtyLauncher
{
    public List<PtySpawnOptions> Options { get; } = new();

    public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
    {
        Options.Add(o);
        return Task.FromResult<IPtySession>(new FakePty());
    }
}
