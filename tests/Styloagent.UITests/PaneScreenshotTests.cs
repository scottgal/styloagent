using Avalonia.Controls;
using Avalonia.Threading;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class PaneScreenshotTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public PaneScreenshotTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class OnePtyLauncher : IPtyLauncher
    {
        private readonly IPtySession _pty;
        public OnePtyLauncher(IPtySession pty) => _pty = pty;
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default) => Task.FromResult(_pty);
    }
    private sealed class NoWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default) => Task.FromResult(false);
    }

    // Screenshots the REAL AgentPaneView (terminal hosted in the pane's Grid, spawned via the
    // same PtyStarted->Attach wiring the app uses) so we see what the app actually shows.
    [Fact]
    public async Task AgentPaneView_shows_terminal_output()
    {
        const string path = "/tmp/styloagent-pane.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        var pty = new FakePtySession();

        await _fx.DispatchAsync(async () =>
        {
            var manifest = new AgentManifestEntry(
                "test-", "", System.IO.Path.GetTempPath(), "", "", "", AgentTransport.Local);
            var session = new AgentSession(manifest, new OnePtyLauncher(pty), new NoWatcher());
            var vm = new AgentPaneViewModel(session, manifest, "test", "#7C5CBF");
            var view = new AgentPaneView { DataContext = vm };
            var window = new Window { Width = 720, Height = 380, Content = view };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Spawn: raises PtyStarted -> the view attaches the TerminalControl to the pty.
            await vm.SpawnCommand.ExecuteAsync(null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            pty.FireOutput("STYLOAGENT PANE TERMINAL\r\n> can you see me?\r\n 1. Yes, I trust this folder\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ScreenshotCapture.CaptureControlAsync(window, view, path);
            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "pane screenshot should be written");
    }
}
