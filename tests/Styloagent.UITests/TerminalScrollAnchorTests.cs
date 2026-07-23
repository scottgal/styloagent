using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Terminal;

namespace Styloagent.UITests;

/// <summary>
/// P0 regression: scrolling UP into scrollback showed "garbled" (shifting) text. Root cause — once the VT
/// scrollback cap is hit and lines EVICT while the agent keeps producing output, the scroll surface is
/// keyed to ABSOLUTE buffer row indices, so every remaining line's pixel offset shifts up by the evicted
/// count while the ScrollViewer offset is preserved — a scrolled-up operator's view jumps forward on every
/// output batch. Fix: TerminalControl subscribes to the engine's Trimmed (eviction) event and, when NOT
/// following the tail, pulls the scroll offset up by the trimmed height so the same content stays anchored.
/// </summary>
[Collection("Avalonia")]
public class TerminalScrollAnchorTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TerminalScrollAnchorTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static async Task Drain()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static string InlineText(SelectableTextBlock tb)
    {
        var sb = new StringBuilder();
        if (tb.Inlines is null) return "";
        foreach (var inline in tb.Inlines) if (inline is Run r) sb.Append(r.Text);
        return sb.ToString();
    }

    /// <summary>The first "LINE_nnnn" marker in the rendered slice — the line at the top of the viewport.</summary>
    private static string TopMarker(string rendered)
    {
        foreach (var raw in rendered.Split('\n'))
        {
            var line = raw.TrimEnd();
            int idx = line.IndexOf("LINE_", System.StringComparison.Ordinal);
            if (idx >= 0) return line.Substring(idx, System.Math.Min(9, line.Length - idx));
        }
        return "(none)";
    }

    [Fact]
    public Task ScrolledUp_ContentStaysAnchored_WhenScrollbackEvicts()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var view = new TerminalControl();
            var window = new Window { Width = 720, Height = 480, Content = view, Name = "EvictAnchor" };
            window.Show();
            await Drain();
            view.Attach(fake);

            // Fill past the scrollback cap so eviction is active.
            var sb = new StringBuilder();
            for (int i = 0; i < 1100; i++) sb.Append($"LINE_{i:0000} scrollback content row\r\n");
            fake.FireOutput(sb.ToString());
            await Drain();

            var sv = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ScrollArea");
            var screen = view.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");

            // Operator scrolls up to read scrollback and stops.
            sv.Offset = sv.Offset.WithY(200 * 16.0);
            await Drain();
            string before = TopMarker(InlineText(screen));
            Assert.StartsWith("LINE_", before);

            // The agent keeps working: 60 more lines arrive, evicting the 60 oldest scrollback rows.
            var more = new StringBuilder();
            for (int i = 1100; i < 1160; i++) more.Append($"LINE_{i:0000} scrollback content row\r\n");
            fake.FireOutput(more.ToString());
            await Drain();

            // The operator did NOT scroll — the line at the top of the viewport must be UNCHANGED (anchored),
            // not shifted forward by the evicted count.
            string after = TopMarker(InlineText(screen));
            Assert.Equal(before, after);

            window.Close();
        });
    }

    [Fact]
    public Task FollowingTail_StillTracksNewOutput_ThroughEviction()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var view = new TerminalControl();
            var window = new Window { Width = 720, Height = 480, Content = view, Name = "EvictTail" };
            window.Show();
            await Drain();
            view.Attach(fake);

            var sb = new StringBuilder();
            for (int i = 0; i < 1100; i++) sb.Append($"LINE_{i:0000} scrollback content row\r\n");
            fake.FireOutput(sb.ToString());
            await Drain();

            // At the tail: the anchor compensation must NOT hold us back — newest output stays visible.
            fake.FireOutput("TAIL_MARKER_LAST_LINE\r\n");
            await Drain();

            var screen = view.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");
            Assert.Contains("TAIL_MARKER_LAST_LINE", InlineText(screen));

            window.Close();
        });
    }

    [Fact]
    public Task ConcurrentPtyOutput_AndWheelScrolling_NeverMixBufferGenerations()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var view = new TerminalControl();
            var window = new Window { Width = 720, Height = 420, Content = view, Name = "ConcurrentScroll" };
            window.Show();
            await Drain();
            view.Attach(fake);

            for (int i = 0; i < 300; i++)
                fake.FireOutput($"LINE_{i:0000} ABCDEFGHIJKLMNOPQRSTUVWXYZ\r\n");
            await Drain();
            Assert.True(view.HandleWheelScroll(30));

            // Production PTY callbacks arrive on a reader thread, unlike the older regression tests which
            // invoked FakePtySession on Avalonia's UI thread. Keep mutating XTerm while the UI repeatedly
            // rebuilds different virtualized slices—the race that used to splice rows/cells from different
            // buffer generations and display corrupted historical text.
            var writer = Task.Run(async () =>
            {
                for (int i = 300; i < 700; i++)
                {
                    fake.FireOutput($"LINE_{i:0000} ABCDEFGHIJKLMNOPQRSTUVWXYZ\r\n");
                    if (i % 4 == 0) await Task.Delay(1);
                }
            });

            var screen = view.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");
            for (int i = 0; i < 80; i++)
            {
                view.HandleWheelScroll(i % 2 == 0 ? 2 : -1);
                await Drain();
                AssertWholeMarkerRows(InlineText(screen));
                await Task.Delay(1);
            }
            await writer;
            await Drain();
            AssertWholeMarkerRows(InlineText(screen));
            window.Close();
        });
    }

    [Fact]
    public Task WheelImmediatelyAfterPtyWrite_ReconcilesTheNewBufferBeforePainting()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var view = new TerminalControl();
            var window = new Window { Width = 720, Height = 420, Content = view };
            window.Show(); await Drain(); view.Attach(fake);
            for (int i = 0; i < 300; i++) fake.FireOutput($"LINE_{i:0000} ABCDEFGHIJKLMNOPQRSTUVWXYZ\r\n");
            await Drain();

            // Deliberately do NOT drain: this is the production race where the VT state changed but the
            // coalesced Render-priority rebuild has not run when the operator starts scrolling upward.
            fake.FireOutput("LINE_0300 ABCDEFGHIJKLMNOPQRSTUVWXYZ\r\n");
            Assert.True(view.HandleWheelScroll(3));
            await Drain();

            var screen = view.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");
            AssertWholeMarkerRows(InlineText(screen));
            window.Close();
        });
    }

    private static void AssertWholeMarkerRows(string rendered)
    {
        foreach (var raw in rendered.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            Assert.StartsWith("LINE_", line);
            Assert.True(line.Length >= 36, $"partially rendered/corrupt scrollback row: '{line}'");
            Assert.True(int.TryParse(line.AsSpan(5, 4), out _), $"corrupt marker in row: '{line}'");
            Assert.EndsWith("ABCDEFGHIJKLMNOPQRSTUVWXYZ", line);
        }
    }
}
