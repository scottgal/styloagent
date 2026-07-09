using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

public class AgentSessionTests
{
    private sealed class FakePty : IPtySession
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
        public void EmitExit() => Exited?.Invoke();
    }

    private sealed class FakeLauncher : IPtyLauncher
    {
        public List<FakePty> Spawned { get; } = new();
        public PtySpawnOptions? Last { get; private set; }
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
        {
            Last = o; var p = new FakePty(); Spawned.Add(p); return Task.FromResult<IPtySession>(p);
        }
    }

    private sealed class FakeWatcher : IFileWatcher
    {
        public bool WillChange = true;
        public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(WillChange);
    }

    private static AgentManifestEntry Entry() => new(
        "foss-", "/repo", "/repo/wt-foss", "/ch/lp/foss.md", "/ch/lp/foss-restart.md",
        "/ch/sc/foss-context.md", AgentTransport.Local);

    [Fact]
    public async Task Spawn_launches_in_worktree_and_sends_prompt()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher());

        await s.SpawnAsync("LAUNCH PROMPT");

        Assert.Equal(SessionState.Live, s.State);
        Assert.Equal("/repo/wt-foss", launcher.Last!.WorkingDirectory);
        Assert.Contains(launcher.Spawned[0].Writes, w => w.Contains("LAUNCH PROMPT"));
    }

    [Fact]
    public async Task Dehydrate_with_ack_disposes_pty_and_sets_state()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await s.SpawnAsync("LP");

        var ok = await s.DehydrateAsync(TimeSpan.FromSeconds(1));

        Assert.True(ok);
        Assert.Equal(SessionState.Dehydrated, s.State);
        Assert.True(launcher.Spawned[0].Disposed);
    }

    [Fact]
    public async Task Dehydrate_without_ack_keeps_session_live()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = false });
        await s.SpawnAsync("LP");

        var ok = await s.DehydrateAsync(TimeSpan.FromMilliseconds(50));

        Assert.False(ok);
        Assert.Equal(SessionState.Live, s.State);
        Assert.False(launcher.Spawned[0].Disposed);   // never lose context
    }

    [Fact]
    public async Task Rehydrate_spawns_new_pty_and_sends_restart()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await s.SpawnAsync("LP");
        await s.DehydrateAsync(TimeSpan.FromSeconds(1));

        await s.RehydrateAsync("RESTART PROMPT");

        Assert.Equal(SessionState.Live, s.State);
        Assert.Equal(2, launcher.Spawned.Count);
        Assert.Contains(launcher.Spawned[1].Writes, w => w.Contains("RESTART PROMPT"));
    }
}
