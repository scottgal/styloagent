using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

[CollectionDefinition("Agent session timing", DisableParallelization = true)]
public class AgentSessionTimingCollection;

[Collection("Agent session timing")]
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
        Assert.Equal("claude", launcher.Last!.Command);
        Assert.Equal("/repo/wt-foss", launcher.Last!.WorkingDirectory);
        Assert.Contains(launcher.Spawned[0].Writes, w => w.Contains("LAUNCH PROMPT"));
    }

    [Fact]
    public async Task Spawn_uses_codex_command_for_codex_runtime()
    {
        var launcher = new FakeLauncher();
        var entry = Entry() with { Runtime = AgentRuntimeKind.Codex };
        var s = new AgentSession(entry, launcher, new FakeWatcher());

        await s.SpawnAsync("LAUNCH PROMPT");

        Assert.Equal("codex", launcher.Last!.Command);
        Assert.Equal("/repo/wt-foss", launcher.Last.WorkingDirectory);
        Assert.Equal("LAUNCH PROMPT", launcher.Last.Args[^1]);
        Assert.Empty(launcher.Spawned[0].Writes);
    }

    [Fact]
    public async Task Spawn_passes_codex_start_prompt_as_native_cli_argument()
    {
        var launcher = new FakeLauncher();
        var entry = Entry() with { Runtime = AgentRuntimeKind.Codex };
        var s = new AgentSession(entry, launcher, new FakeWatcher(), ["--sandbox", "workspace-write"]);

        await s.SpawnAsync("START CODEX WORK");

        Assert.Equal(["--sandbox", "workspace-write", "START CODEX WORK"], launcher.Last!.Args);
        Assert.Empty(launcher.Spawned[0].Writes);
    }

    [Fact]
    public async Task Spawn_submits_prompt_with_carriage_return_not_newline()
    {
        // Regression: Claude's TUI submits on Enter (0x0D "\r"). A trailing "\n" only inserts a
        // newline in the input box, leaving the prompt typed-but-unsent — which also blocks
        // auto-rehydration. The prompt must be followed by a "\r" submit and no stray "\n".
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher());

        await s.SpawnAsync("LAUNCH PROMPT");

        var writes = launcher.Spawned[0].Writes;
        Assert.Equal("\r", writes[^1]);                              // last write is the Enter submit
        Assert.DoesNotContain(writes, w => w.Contains('\n'));        // nothing left unsent via "\n"
    }

    [Fact]
    public async Task Spawn_passes_launch_args_to_the_launcher()
    {
        var launcher = new FakeLauncher();
        var args = new[] { "--settings", "{\"hooks\":{}}" };
        var s = new AgentSession(Entry(), launcher, new FakeWatcher(), args);

        await s.SpawnAsync("hi");

        Assert.Equal(args, launcher.Last!.Args);
    }

    [Fact]
    public async Task Spawn_defaults_to_no_extra_args()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher());

        await s.SpawnAsync("hi");

        Assert.Empty(launcher.Last!.Args);
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

    [Fact]
    public async Task Rehydrate_passes_codex_restart_prompt_as_native_cli_argument()
    {
        var launcher = new FakeLauncher();
        var entry = Entry() with { Runtime = AgentRuntimeKind.Codex };
        var s = new AgentSession(entry, launcher, new FakeWatcher { WillChange = true });
        await s.SpawnAsync("START");
        await s.DehydrateAsync(TimeSpan.FromSeconds(1));

        await s.RehydrateAsync("RESUME CODEX WORK");

        Assert.Equal("RESUME CODEX WORK", launcher.Last!.Args[^1]);
        Assert.Empty(launcher.Spawned[1].Writes);
    }

    [Fact]
    public async Task Spawn_sets_CurrentPty_and_raises_PtyStarted()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher());

        IPtySession? raised = null;
        s.PtyStarted += pty => raised = pty;

        await s.SpawnAsync("LP");

        Assert.NotNull(s.CurrentPty);
        Assert.Same(launcher.Spawned[0], s.CurrentPty);
        Assert.Same(s.CurrentPty, raised);
    }

    [Fact]
    public async Task Dehydrate_clears_CurrentPty()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await s.SpawnAsync("LP");
        Assert.NotNull(s.CurrentPty);

        await s.DehydrateAsync(TimeSpan.FromSeconds(1));

        Assert.Null(s.CurrentPty);
    }

    [Fact]
    public async Task Rehydrate_sets_CurrentPty_and_raises_PtyStarted()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = true });
        await s.SpawnAsync("LP");
        await s.DehydrateAsync(TimeSpan.FromSeconds(1));

        IPtySession? raised = null;
        s.PtyStarted += pty => raised = pty;

        await s.RehydrateAsync("RESTART PROMPT");

        Assert.NotNull(s.CurrentPty);
        Assert.Same(launcher.Spawned[1], s.CurrentPty);
        Assert.Same(s.CurrentPty, raised);
    }

    [Fact]
    public async Task Dehydrate_without_ack_does_not_clear_CurrentPty()
    {
        var launcher = new FakeLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher { WillChange = false });
        await s.SpawnAsync("LP");
        var originalPty = s.CurrentPty;

        await s.DehydrateAsync(TimeSpan.FromMilliseconds(50));

        Assert.NotNull(s.CurrentPty);
        Assert.Same(originalPty, s.CurrentPty);
    }

    /// <summary>
    /// A PTY whose child paints its TUI only after a delay, and which records whether any input
    /// arrived before that first paint.
    /// </summary>
    private sealed class BootingPty : IPtySession
    {
        public List<string> Writes { get; } = new();
        public Action<string>? OnWrite { get; set; }
        public bool Disposed { get; private set; }
        public bool SawWriteBeforeFirstPaint { get; private set; }
        public bool Painted { get; private set; }
        public event Action<string>? Output;
#pragma warning disable CS0067
        public event Action? Exited;
#pragma warning restore CS0067
        public bool IsIdle => true;
        public void Resize(int cols, int rows) { }

        /// <summary>Simulates the child's TUI producing its first frame.</summary>
        public void Paint() { Painted = true; Output?.Invoke("[2J kilo ready "); }

        public ValueTask WriteAsync(string text, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!Painted) SawWriteBeforeFirstPaint = true;
            Writes.Add(text);
            OnWrite?.Invoke(text);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class BootingLauncher : IPtyLauncher
    {
        public List<BootingPty> Spawned { get; } = new();
        public BootingPty Pty => Spawned[^1];
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
        {
            var pty = new BootingPty();
            Spawned.Add(pty);
            return Task.FromResult<IPtySession>(pty);
        }
    }

    /// <summary>Initial prompts are held until an interactive CLI has painted its first frame.</summary>
    [Fact]
    public async Task Prompt_is_not_typed_before_the_child_tui_has_painted()
    {
        var launcher = new BootingLauncher();
        var s = new AgentSession(Entry(), launcher, new FakeWatcher());

        AgentSession.InjectBootTimeout = TimeSpan.FromSeconds(5);
        try
        {
            var spawn = s.SpawnAsync("LAUNCH PROMPT");
            // The child is still booting: let the spawn path run, then paint.
            await Task.Delay(150);
            launcher.Pty.Paint();
            await spawn;
        }
        finally
        {
            AgentSession.InjectBootTimeout = TimeSpan.Zero;
        }

        Assert.Contains(launcher.Pty.Writes, w => w.Contains("LAUNCH PROMPT"));
        Assert.False(launcher.Pty.SawWriteBeforeFirstPaint,
            "the prompt was typed before the TUI painted — kilo discards those bytes and the agent " +
            "never receives its initial prompt");
    }

    [Theory]
    [InlineData(AgentRuntimeKind.Claude, false)]
    [InlineData(AgentRuntimeKind.Claude, true)]
    [InlineData(AgentRuntimeKind.ClaudeDeepSeek, false)]
    [InlineData(AgentRuntimeKind.ClaudeDeepSeek, true)]
    public async Task Resume_and_restart_wait_for_the_new_process_to_paint(AgentRuntimeKind runtime, bool restart)
    {
        var launcher = new BootingLauncher();
        var session = new AgentSession(Entry() with { Runtime = runtime }, launcher, new FakeWatcher());
        var previousTimeout = AgentSession.InjectBootTimeout;
        try
        {
            await session.SpawnAsync("");
            var old = launcher.Pty;
            old.Paint();
            if (restart) await session.KillAsync();
            else Assert.True(await session.DehydrateAsync(TimeSpan.FromSeconds(1)));

            AgentSession.InjectBootTimeout = TimeSpan.FromSeconds(5);
            var start = restart ? session.SpawnAsync("RESUME") : session.RehydrateAsync("RESUME");
            try
            {
                Assert.False(start.IsCompleted);
                Assert.Empty(launcher.Pty.Writes);
                old.Paint(); // Late output from the previous process cannot satisfy readiness.
                Assert.Empty(launcher.Pty.Writes);
            }
            finally { launcher.Pty.Paint(); await start; }

            Assert.False(launcher.Pty.SawWriteBeforeFirstPaint);
            Assert.Collection(launcher.Pty.Writes,
                text => Assert.Equal("RESUME", text), text => Assert.Equal("\r", text));
            Assert.Equal(SessionState.Live, session.State);
        }
        finally
        {
            AgentSession.InjectBootTimeout = previousTimeout;
            await session.KillAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_written_during_submission_is_acknowledged(bool existingFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "styloagent-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "context.md");
        var launcher = new BootingLauncher();
        var session = new AgentSession(Entry() with { SavedContextPath = path }, launcher, new FileSystemFileWatcher());
        try
        {
            if (existingFile) await File.WriteAllTextAsync(path, "previous checkpoint");
            await session.SpawnAsync("");
            launcher.Pty.OnWrite = text =>
            {
                if (text == "\r") File.WriteAllText(path, "new checkpoint written during submission");
            };

            Assert.True(await session.DehydrateAsync(TimeSpan.FromMilliseconds(500)));
            Assert.Equal(SessionState.Dehydrated, session.State);
            Assert.True(launcher.Pty.Disposed);
        }
        finally { await session.KillAsync(); Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_checkpoint_submission_cancels_the_watch_and_keeps_session_live(bool cancel)
    {
        var watcher = new PendingWatcher();
        var launcher = new BootingLauncher();
        var session = new AgentSession(Entry(), launcher, watcher);
        using var cts = new CancellationTokenSource();
        try
        {
            await session.SpawnAsync("");
            launcher.Pty.OnWrite = _ =>
            {
                if (cancel) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
                throw new IOException("submission failed");
            };
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DehydrateAsync(TimeSpan.FromSeconds(1), cts.Token));
            else
                await Assert.ThrowsAsync<IOException>(() => session.DehydrateAsync(TimeSpan.FromSeconds(1), cts.Token));

            Assert.True(watcher.Stopped);
            Assert.Equal(SessionState.Live, session.State);
            Assert.False(launcher.Pty.Disposed);
        }
        finally { await session.KillAsync(); }
    }

    [Fact]
    public async Task Checkpoint_timeout_stops_the_watch_and_keeps_session_live()
    {
        var watcher = new PendingWatcher();
        var session = new AgentSession(Entry(), new BootingLauncher(), watcher);
        try
        {
            await session.SpawnAsync("");
            Assert.False(await session.DehydrateAsync(TimeSpan.FromMilliseconds(50)));
            Assert.True(watcher.Stopped);
            Assert.Equal(SessionState.Live, session.State);
        }
        finally { await session.KillAsync(); }
    }

    private sealed class PendingWatcher : IFileWatcher
    {
        public bool Stopped { get; private set; }
        public async Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(timeout, ct);
                return false;
            }
            finally { Stopped = true; }
        }
    }
}
