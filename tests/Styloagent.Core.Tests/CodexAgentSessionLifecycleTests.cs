using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

public class CodexAgentSessionLifecycleTests
{
    [Fact]
    public async Task Dehydrate_injects_checkpoint_prompt_then_parks_only_after_ack()
    {
        var launcher = new FakeLauncher();
        var session = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await session.SpawnAsync("START");

        var parked = await session.DehydrateAsync(TimeSpan.FromSeconds(1));

        Assert.True(parked);
        Assert.Contains(launcher.Pty.Writes,
            write => write.Contains("Please checkpoint your context to /ch/sc/codex-context.md", StringComparison.Ordinal));
        Assert.Equal("\r", launcher.Pty.Writes[^1]);
        Assert.True(launcher.Pty.Disposed);
        Assert.Equal(SessionState.Dehydrated, session.State);
    }

    [Fact]
    public async Task Rehydrate_starts_a_new_codex_with_the_resume_prompt_argument()
    {
        var launcher = new FakeLauncher();
        var session = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await session.SpawnAsync("START");
        await session.DehydrateAsync(TimeSpan.FromSeconds(1));

        await session.RehydrateAsync("Read /ch/sc/codex-context.md and resume.");

        Assert.Equal("codex", launcher.Options[^1].Command);
        Assert.Equal("Read /ch/sc/codex-context.md and resume.", launcher.Options[^1].Args[^1]);
        Assert.Empty(launcher.Ptys[^1].Writes);
        Assert.Equal(SessionState.Live, session.State);
    }

    private static AgentManifestEntry Entry() => new(
        "codex-", "/repo", "/repo/wt-codex", "", "", "/ch/sc/codex-context.md",
        AgentTransport.Local, AgentRuntimeKind.Codex);

    private sealed class FakeWatcher : IFileWatcher
    {
        public bool WillChange { get; init; }
        public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(WillChange);
    }

    private sealed class FakeLauncher : IPtyLauncher
    {
        public List<PtySpawnOptions> Options { get; } = [];
        public List<FakePty> Ptys { get; } = [];
        public FakePty Pty => Ptys[0];

        public Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
        {
            Options.Add(options);
            var pty = new FakePty();
            Ptys.Add(pty);
            return Task.FromResult<IPtySession>(pty);
        }
    }

    private sealed class FakePty : IPtySession
    {
        public List<string> Writes { get; } = [];
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
}
