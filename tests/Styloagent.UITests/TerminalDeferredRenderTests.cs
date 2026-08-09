using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Terminal;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// The fleet-busy UI fix: only the ACTIVE (focused) terminal renders eagerly at frame rate; background
/// terminals defer their rebuild to a shared slow tick so their combined cost can't saturate the UI
/// thread while the operator types. The agents themselves keep running — the VT engine state updates
/// eagerly off-thread, so a deferred flush always renders the LATEST buffer.
/// </summary>
[Collection("Avalonia")]
public class TerminalDeferredRenderTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TerminalDeferredRenderTests(HeadlessAvaloniaFixture fx) => _fx = fx;

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

    private static SelectableTextBlock ScreenOf(TerminalControl control)
        => control.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");

    [Fact]
    public Task Inactive_terminal_defers_renders_but_keeps_streaming_and_flushes_on_focus()
    {
        return _fx.DispatchAsync(async () =>
        {
            var originalInterval = Styloagent.Terminal.TerminalControl.DeferredRendersIntervalMs;
            try
            {
                // Stretch the slow tick so it can never fire mid-test — deferral is proven by absence.
                Styloagent.Terminal.TerminalControl.DeferredRendersIntervalMs = 60_000;

                var a = new TerminalControl { Name = "TermA" };
                var b = new TerminalControl { Name = "TermB" };
                var stack = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children = { a, b },
                };
                var window = new Window { Width = 800, Height = 600, Content = stack };
                window.Show();
                await Drain();

                var fakeA = new FakePtySession();
                var fakeB = new FakePtySession();
                a.Attach(fakeA);
                b.Attach(fakeB);
                await Drain();

                // Headless Avalonia doesn't raise real focus events, so drive the terminal's actual
                // OnLostFocus/OnGotFocus handlers directly (A loses focus, B holds it).
                a.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
                b.RaiseEvent(new Avalonia.Input.GotFocusEventArgs());
                await Drain();

                // The INACTIVE terminal's output is NOT rendered eagerly…
                fakeA.FireOutput("STREAMING_LINE_1\r\n");
                await Drain();
                Assert.DoesNotContain("STREAMING_LINE_1", InlineText(ScreenOf(a)));

                // …but the ACTIVE terminal's is.
                fakeB.FireOutput("ACTIVE_LINE\r\n");
                await Drain();
                Assert.Contains("ACTIVE_LINE", InlineText(ScreenOf(b)));

                // The agent keeps running while deferred: more output arrives, engine state stays current.
                fakeA.FireOutput("STREAMING_LINE_2\r\nSTREAMING_LINE_3\r\n");
                await Drain();
                Assert.DoesNotContain("STREAMING_LINE_2", InlineText(ScreenOf(a)));

                // Focusing the background pane flushes EVERYTHING that streamed while it was deferred —
                // nothing is lost, and the flush shows the latest buffer (agents never stall).
                a.RaiseEvent(new Avalonia.Input.GotFocusEventArgs());
                await Drain();
                var text = InlineText(ScreenOf(a));
                Assert.Contains("STREAMING_LINE_1", text);
                Assert.Contains("STREAMING_LINE_2", text);
                Assert.Contains("STREAMING_LINE_3", text);

                window.Close();
            }
            finally
            {
                Styloagent.Terminal.TerminalControl.DeferredRendersIntervalMs = originalInterval;
            }
        });
    }
}
