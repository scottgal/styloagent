using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Mostlylucid.Avalonia.UITesting.Players;
using SkiaSharp;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Hooks;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class AgentRosterBadgeTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AgentRosterBadgeTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class NullLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class NullWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    /// <summary>Launcher that hands back a caller-supplied PTY so the test can kill it via FireExited.</summary>
    private sealed class OnePtyLauncher : IPtyLauncher
    {
        private readonly IPtySession _pty;
        public OnePtyLauncher(IPtySession pty) => _pty = pty;
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default) => Task.FromResult(_pty);
    }

    private static AgentPaneViewModel MakePane()
    {
        var entry = new AgentManifestEntry(
            "foss-", "/repo", "/repo/wt", "", "", "", AgentTransport.Local);
        var session = new AgentSession(entry, new NullLauncher(), new NullWatcher());
        return new AgentPaneViewModel(session, entry, "foss", "#E57373");
    }

    // Renders the REAL roster row template (extracted to a resource) against a "needs you" pane and
    // asserts amber (#FFCC33) pixels appear — proving the §4.4 badge bindings resolve and the
    // waiting-for-human highlight is visible. A single control materializes headless (an
    // ItemsControl does not), so we build the template directly.
    [Fact]
    public async Task Roster_row_shows_amber_needs_you_badge()
    {
        const string path = "/tmp/styloagent-roster-badge.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        await _fx.DispatchAsync(async () =>
        {
            var pane = MakePane();
            pane.ApplyHookEvent(new HookEvent("foss", "Notification", "permission_prompt", "Allow Bash?", null, null));
            pane.IsSelected = true; // also exercises the selection-outline binding in a real render
            Assert.True(pane.NeedsYou);

            var template = (IDataTemplate)new AgentsView().Resources["AgentRowTemplate"]!;
            var host = new ContentControl
            {
                Width = 240,
                Height = 48,
                ContentTemplate = template,
                Content = pane,
                Background = Avalonia.Media.Brushes.Black,
            };
            var window = new Window { Width = 260, Height = 64, Content = host };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ScreenshotCapture.CaptureControlAsync(window, host, path);
            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "roster badge screenshot should be written");

        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        int amber = 0;
        for (int y = 0; y < bmp!.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            // #FFCC33 amber glyph/text/dot: strong red+green, low blue.
            if (p.Red > 200 && p.Green > 150 && p.Blue < 120) amber++;
        }
        Assert.True(amber > 30, $"Expected amber 'needs you' badge pixels, found {amber}.");
    }

    // Fix 2 — UX-level proof: drives a pane into the stuck ⚠ needs-you state, hard-kills its PTY (which
    // fires NO SessionEnd hook), then renders the REAL roster row template. The row must flip to the red
    // ✕ "exited" badge with the amber needs-you highlight GONE — i.e. a killed agent stops hanging on ⚠
    // in the actual rendered UI, not just in the view-model property.
    [Fact]
    public async Task Roster_row_flips_from_needsyou_to_exited_when_the_pty_is_killed()
    {
        const string path = "/tmp/styloagent-roster-exited.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        await _fx.DispatchAsync(async () =>
        {
            var pty = new FakePtySession();
            var entry = new AgentManifestEntry("foss-", "/repo", "/repo/wt", "", "", "", AgentTransport.Local);
            var session = new AgentSession(entry, new OnePtyLauncher(pty), new NullWatcher());
            var pane = new AgentPaneViewModel(session, entry, "foss", "#E57373");

            await pane.SpawnAsync();   // wires the PTY; the pane subscribes to its Exited signal
            pane.ApplyHookEvent(new HookEvent("foss", "Notification", "permission_prompt", "Allow Bash?", null, null));
            Assert.True(pane.NeedsYou);   // ⚠ blocked on a human

            pty.FireExited();             // hard kill — no SessionEnd hook fires
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(AgentHookState.Exited, pane.HookState);
            Assert.False(pane.NeedsYou);

            // Render the REAL roster row template against the killed pane.
            var template = (IDataTemplate)new AgentsView().Resources["AgentRowTemplate"]!;
            var host = new ContentControl
            {
                Width = 240,
                Height = 48,
                ContentTemplate = template,
                Content = pane,
                Background = Avalonia.Media.Brushes.Black,
            };
            var window = new Window { Width = 260, Height = 64, Content = host };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ScreenshotCapture.CaptureControlAsync(window, host, path);
            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "exited-row screenshot should be written");

        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        int red = 0, amber = 0;
        for (int y = 0; y < bmp!.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            // #C05555 exited glyph/text: red-dominant, muted green/blue.
            if (p.Red > 150 && p.Green < 120 && p.Blue < 120) red++;
            // #FFCC33 needs-you amber: strong red+green, low blue — must be GONE after the kill.
            if (p.Red > 200 && p.Green > 150 && p.Blue < 120) amber++;
        }
        Assert.True(red > 30, $"Expected red ✕ 'exited' badge pixels in the rendered roster row, found {red}.");
        Assert.True(amber < 10, $"The amber ⚠ 'needs you' highlight must be gone after the kill, but found {amber} amber pixels.");
    }
}
