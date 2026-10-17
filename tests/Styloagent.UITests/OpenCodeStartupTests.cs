using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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

    private static bool IsRunning(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class RecordingLauncher : IPtyLauncher
    {
        public IPtySession? Terminal;
        public PtySpawnOptions? Options;
        public async Task<IPtySession> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
        {
            Options = options;
            return Terminal = await new PortaPtyLauncher().SpawnAsync(options, ct);
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
        public void Resize(int cols, int rows) => inner.Resize(cols, rows);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask WriteAsync(string text, CancellationToken ct = default) { Writes.Enqueue(text); return inner.WriteAsync(text, ct); }
    }
}
