using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class OpenCodeStartupTests(HeadlessAvaloniaFixture fixture)
{
    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("STYLOAGENT_LIVE_OPENCODE") != "1")
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
        }
        finally { Directory.Delete(root, recursive: true); }
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
