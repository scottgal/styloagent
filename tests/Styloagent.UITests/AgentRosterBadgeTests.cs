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
}
