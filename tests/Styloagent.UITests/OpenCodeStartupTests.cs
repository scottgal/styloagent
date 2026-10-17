using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Mostlylucid.Avalonia.UITesting.Players;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Core.Sessions;
using Styloagent.Core.Model;
using Styloagent.Core.Hooks;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class OpenCodeStartupTests(HeadlessAvaloniaFixture fixture)
{
    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Native UI process probes currently use POSIX shell commands.";
            else if (Environment.GetEnvironmentVariable("STYLOAGENT_LIVE_OPENCODE") != "1")
                Skip = "Opt in to real OpenCode startup with STYLOAGENT_LIVE_OPENCODE=1 (no provider calls).";
        }
    }

    [LiveFact]
    public async Task Real_opencode_startup_paints_the_terminal_after_late_UI_attachment()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-opencode-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            await using var session = await OpenCodeSession.StartAsync(
                new OpenCodeOptions("ui-startup-", root, "ui-startup", StateDirectory: Path.Combine(root, "state")),
                new PtySpawnOptions("opencode", [], root, null, 100, 30), new PortaPtyLauncher(), "", null, null, deadline.Token);
            Assert.Equal("deepseek/deepseek-flash", session.Model);
            // Existing-project startup launches the TUI before the cockpit attaches its view.
            await Task.Delay(500, deadline.Token);
            await fixture.DispatchAsync(async () =>
            {
                var terminal = new TerminalControl();
                var window = new Window { Content = terminal, Width = 1000, Height = 650 };
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                try
                {
                    var trace = new TracedSession(session);
                    terminal.Attach(trace);
                    for (var i = 0; i < 100 && terminal.RenderedText.Count(char.IsLetterOrDigit) < 40; i++)
                        await Task.Delay(100, deadline.Token);
                    Assert.True(terminal.RenderedText.Count(char.IsLetterOrDigit) >= 40,
                        "OpenCode never painted its initial screen through the real TerminalControl. Screen: " + terminal.RenderedText + "\nWrites: " + string.Join(" | ", trace.Writes.Select(w => System.Text.Json.JsonSerializer.Serialize(w))));
                    terminal.RaiseEvent(new TextInputEventArgs
                    {
                        RoutedEvent = InputElement.TextInputEvent,
                        Text = "STARTUP_INPUT_PROBE",
                    });
                    for (var i = 0; i < 50 && !terminal.RenderedText.Contains("STARTUP_INPUT_PROBE"); i++)
                        await Task.Delay(100, deadline.Token);
                    Assert.Contains("STARTUP_INPUT_PROBE", terminal.RenderedText);
                    terminal.Detach();
                    terminal.Attach(session);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.Contains("STARTUP_INPUT_PROBE", terminal.RenderedText);
                }
                finally { window.Close(); }
            });
            var sessionId = session.SessionId;
            await session.DisposeAsync();
            await using var restored = await OpenCodeSession.StartAsync(
                new OpenCodeOptions("ui-startup-", root, "ui-startup", StateDirectory: Path.Combine(root, "state")),
                new PtySpawnOptions("opencode", [], root, null, 100, 30), new PortaPtyLauncher(), "", null, null, deadline.Token);
            Assert.Equal(sessionId, restored.SessionId);
            Assert.Equal("deepseek/deepseek-flash", restored.Model);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [LiveFact]
    public async Task Closing_cockpit_waits_for_real_backend_terminal_and_running_shell_to_exit()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-opencode-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await fixture.DispatchAsync(async () =>
            {
                using var vm = await MainWindowViewModel.InitializeAsync(Path.Combine(root, "channel"),
                    new PortaPtyLauncher(), new FileSystemFileWatcher(), repoRoot: root);
                var launcher = new RecordingLauncher();
                var manifest = new AgentManifestEntry("close-test-", root, root, "", "", "", AgentTransport.Local,
                    AgentRuntimeKind.OpenCode);
                var agent = new AgentSession(manifest, launcher, new FileSystemFileWatcher(),
                    openCodeOptions: new("close-test-", root, "close-test", PermissionMode: FleetPermissionMode.Bypass,
                        StateDirectory: Path.Combine(root, "native")));
                var pane = new AgentPaneViewModel(agent, manifest, "close-test", "#888888");
                vm.Panes.Add(pane);
                var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                try
                {
                    var running = Path.Combine(root, "running.pid");
                    var leaked = Path.Combine(root, "leaked.txt");
                    var childPidFile = Path.Combine(root, "child.pid");
                    await agent.SpawnAsync("", deadline.Token);
                    var native = Assert.IsType<OpenCodeSession>(agent.CurrentPty);
                    using var client = new HttpClient { BaseAddress = native.ServerUrl };
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + launcher.Options!.Env!["OPENCODE_SERVER_PASSWORD"])));
                    // Native session shell API executes real work without a model call or API credits.
                    var shell = client.PostAsJsonAsync($"/api/session/{native.SessionId}/shell",
                        new { command = $"echo $$ > '{running}'; sleep 30 & echo $! > '{childPidFile}'; wait; echo leaked > '{leaked}'" }, deadline.Token);
                    while (!File.Exists(running) || !File.Exists(childPidFile)) await Task.Delay(100, deadline.Token);
                    var shellPid = int.Parse((await File.ReadAllTextAsync(running, deadline.Token)).Trim());
                    var childPid = int.Parse((await File.ReadAllTextAsync(childPidFile, deadline.Token)).Trim());
                    var backendPid = agent.CurrentPty!.ProcessId;
                    var terminalPid = launcher.Terminal!.ProcessId;
                    Assert.True(backendPid > 0 && terminalPid > 0 && shellPid > 0);
                    window.Close();
                    await closed.Task.WaitAsync(deadline.Token);
                    Assert.True(vm.IsStopped);
                    foreach (var pid in new[] { backendPid, terminalPid, shellPid, childPid })
                    {
                        for (var i = 0; i < 30 && IsRunning(pid); i++) await Task.Delay(100, deadline.Token);
                        Assert.False(IsRunning(pid), $"Owned process {pid} survived cockpit close.");
                    }
                    Assert.False(File.Exists(leaked));
                    try { using var response = await shell; }
                    catch (HttpRequestException) { /* server closed after cancellation */ }
                }
                finally
                {
                    await vm.StopAsync();
                    window.Close();
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [LiveFact]
    public async Task Real_opencode_wheel_scrolls_history_without_moving_terminal_viewport()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-opencode-scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var launcher = new RecordingLauncher();
            await using var session = await OpenCodeSession.StartAsync(
                new OpenCodeOptions("ui-scroll-", root, "ui-scroll", StateDirectory: Path.Combine(root, "state")),
                new PtySpawnOptions("opencode", [], root, null, 100, 30), launcher, "", null, null, deadline.Token);
            using var client = new HttpClient { BaseAddress = session.ServerUrl };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + launcher.Options!.Env!["OPENCODE_SERVER_PASSWORD"])));
            await fixture.DispatchAsync(async () =>
            {
                var terminal = new TerminalControl();
                var window = new Window { Content = terminal, Width = 1000, Height = 650 };
                window.Show();
                try
                {
                    terminal.Attach(session);
                    await Task.Delay(1000, deadline.Token);
                    int block = 0;
                    do
                    {
                        using var response = await client.PostAsJsonAsync($"/api/session/{session.SessionId}/shell",
                            new { command = $"i=1; while [ $i -le 8 ]; do printf 'SCROLL_BLOCK_{block:000}_ROW_%02d Content is preserved by default.\\n' $i; i=$((i+1)); done" }, deadline.Token);
                        response.EnsureSuccessStatusCode();
                        await Task.Delay(75, deadline.Token);
                        block++;
                    } while (block < 12 || (launcher.OutputCharacters < 300_000 && block < 400));
                    Assert.True(launcher.OutputCharacters > 256 * 1024,
                        $"Probe must overflow the raw replay cap; emitted {launcher.OutputCharacters} characters.");
                    await Task.Delay(500, deadline.Token);
                    terminal.Detach();
                    // A whole dock view is replaced while OpenCode keeps running, after its raw startup
                    // commands have fallen out of both replay buffers.
                    terminal = new TerminalControl();
                    window.Content = terminal;
                    await HeadlessRender.SettleAsync(window);
                    terminal.Attach(session);
                    await Task.Delay(200, deadline.Token);
                    await HeadlessRender.SettleAsync(window);
                    Assert.True(terminal.IsAltBuffer);
                    var before = terminal.RenderedText;
                    for (int n = 0; n < 50 && !terminal.RenderedText.Contains("SCROLL_BLOCK_000_ROW_03"); n++)
                    {
                        window.MouseWheel(new Avalonia.Point(300, 200), new Avalonia.Vector(0, 100));
                        await Task.Delay(80, deadline.Token);
                    }
                    var after = terminal.RenderedText;
                    Assert.NotEqual(before, after);
                    Assert.Contains("SCROLL_BLOCK_000_ROW_03", after);
                    Assert.Equal(terminal.PtyRows, after.Split('\n').Length);
                    await ScreenshotCapture.CaptureControlAsync(window, terminal,
                        Path.Combine(Path.GetTempPath(), "styloagent-opencode-scrolled.png"));
                    // Keep producing output while reading older rows; the fixed terminal grid must stay
                    // intact and wheel-down must still return to the newest tool result.
                    using var ongoing = await client.PostAsJsonAsync($"/api/session/{session.SessionId}/shell",
                        new { command = "printf 'LIVE_OUTPUT_AFTER_SCROLL\\n'; sleep 0.2; printf 'LIVE_OUTPUT_DONE\\n'" }, deadline.Token);
                    ongoing.EnsureSuccessStatusCode();
                    for (int n = 0; n < 50 && !terminal.RenderedText.Contains("LIVE_OUTPUT_DONE"); n++)
                    {
                        window.MouseWheel(new Avalonia.Point(300, 200), new Avalonia.Vector(0, -100));
                        await Task.Delay(60, deadline.Token);
                    }
                    Assert.Contains("LIVE_OUTPUT_DONE", terminal.RenderedText);
                    var scroll = terminal.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ScrollArea");
                    Assert.Equal(0, scroll.Offset.Y);
                }
                finally { window.Close(); }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [LiveFact]
    public async Task Real_PTY_preserves_startup_modes_before_first_view_after_raw_replay_overflows()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-pty-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var ready = Path.Combine(root, "ready");
            var script = Path.Combine(root, "startup.sh");
            await File.WriteAllTextAsync(script, """
                printf '\033[?1049h\033[?1000h\033[?1006h\033[2J\033[HSTARTUP_HEADER'
                i=0
                while [ "$i" -lt 9000 ]; do
                  printf '\033[3;1H\033[48;2;23;43;50m0671 + Content is preserved by default.\033[0m'
                  i=$((i+1))
                done
                printf '\033[3;1H\033[2KFINAL_LONG_STARTUP'
                touch ready
                sleep 30
                """, deadline.Token);
            await using var pty = await new PortaPtyLauncher().SpawnAsync(
                new PtySpawnOptions("/bin/sh", [script], root, null, 100, 30), deadline.Token);
            while (!File.Exists(ready)) await Task.Delay(20, deadline.Token);
            await Task.Delay(200, deadline.Token); // allow the bounded output publisher to flush
            string replay = "";
            void Capture(string text) => replay += text;
            pty.Output += Capture;
            pty.Output -= Capture;
            Assert.True(replay.Length >= 256 * 1024);
            Assert.DoesNotContain("STARTUP_HEADER", replay);
            Assert.DoesNotContain("\x1b[?1049h", replay);
            await fixture.DispatchAsync(async () =>
            {
                var terminal = new TerminalControl();
                var window = new Window { Content = terminal, Width = 1000, Height = 650 };
                window.Show();
                try
                {
                    terminal.Attach(pty);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.True(terminal.IsAltBuffer);
                    Assert.Contains("STARTUP_HEADER", terminal.ScreenGrid()[0]);
                    Assert.Contains("FINAL_LONG_STARTUP", terminal.ScreenGrid()[2]);
                }
                finally { window.Close(); }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool IsRunning(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class RecordingLauncher : IPtyLauncher
    {
        public IPtySession? Terminal;
        public PtySpawnOptions? Options;
        public long OutputCharacters;
        public async Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
        {
            Options = options;
            Terminal = await new PortaPtyLauncher().SpawnAsync(options, ct);
            Terminal.Output += chunk => Interlocked.Add(ref OutputCharacters, chunk.Length);
            return Terminal;
        }
    }

    private sealed class TracedSession(IPtySession inner) : IPtySession
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Writes = new();
        private Action<string>? _output;
        public event Action<string>? Output
        {
            add { _output += value; inner.Output += Capture; }
            remove { inner.Output -= Capture; _output -= value; }
        }
        private void Capture(string chunk) => _output?.Invoke(chunk);
        public event Action? Exited { add => inner.Exited += value; remove => inner.Exited -= value; }
        public bool IsIdle => inner.IsIdle;
        public IPtySession TerminalSession => inner.TerminalSession;
        public void Resize(int cols, int rows) => inner.Resize(cols, rows);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask WriteAsync(string text, CancellationToken ct = default) { Writes.Enqueue(text); return inner.WriteAsync(text, ct); }
    }
}
