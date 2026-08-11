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
/// The fleet-CPU fix: a background terminal that is not actually ON SCREEN (hidden dock tab, collapsed
/// pane) must do NO render work at all on the shared slow tick.
///
/// Deferral alone was not enough. <c>_isActive</c> tracks FOCUS only, so every unfocused terminal — including
/// panes the operator cannot see — was fully rebuilt 4x/second by <c>DeferredRenders.Tick</c>. Each such
/// rebuild re-materialises the row text AND allocates a fresh Avalonia <see cref="Run"/> (a full StyledElement
/// with a property store and logical-parent wiring) per colour span, then re-shapes the whole slice. Measured
/// on a live 10-agent cockpit that was ~56% of ALL process allocation (~5.8 MB/s sustained), which kept
/// background GC — and a core — permanently busy and made typing in the visible pane lag.
///
/// An off-screen terminal stays registered and dirty; it flushes the moment it becomes visible, so nothing
/// is lost.
/// </summary>
[Collection("Avalonia")]
public class TerminalOffscreenRenderTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TerminalOffscreenRenderTests(HeadlessAvaloniaFixture fx) => _fx = fx;

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
    public Task Offscreen_background_terminal_does_no_rebuild_work_on_the_slow_tick()
    {
        return _fx.DispatchAsync(async () =>
        {
            var originalInterval = TerminalControl.DeferredRendersIntervalMs;
            try
            {
                // Never let the real timer fire — the test drives the tick synchronously.
                TerminalControl.DeferredRendersIntervalMs = 60_000;

                var onscreen = new TerminalControl { Name = "OnScreen" };
                var offscreen = new TerminalControl { Name = "OffScreen" };
                var stack = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children = { onscreen, offscreen },
                };
                var window = new Window { Width = 800, Height = 600, Content = stack };
                window.Show();
                await Drain();

                var onFake = new FakePtySession();
                var offFake = new FakePtySession();
                onscreen.Attach(onFake);
                offscreen.Attach(offFake);
                await Drain();

                // Both are BACKGROUND panes (unfocused) — the deferred-render population.
                onscreen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
                offscreen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));

                // …but one of them is not on screen, exactly like an unselected dock tab.
                offscreen.IsVisible = false;
                await Drain();

                Assert.True(onscreen.IsEffectivelyVisible);
                Assert.False(offscreen.IsEffectivelyVisible);

                onFake.FireOutput("ONSCREEN_LINE\r\n");
                offFake.FireOutput("OFFSCREEN_LINE\r\n");
                await Drain();

                var onBefore = onscreen.RebuildCount;
                var offBefore = offscreen.RebuildCount;

                // Drive the shared slow tick several times, as it would fire while agents stream.
                for (int i = 0; i < 5; i++)
                {
                    TerminalControl.DeferredRendersTickForTest();
                    await Drain();
                }

                // The VISIBLE background pane refreshes on the tick — the operator can see it.
                Assert.True(onscreen.RebuildCount > onBefore,
                    "a visible background terminal must still refresh on the slow tick");
                Assert.Contains("ONSCREEN_LINE", InlineText(ScreenOf(onscreen)));

                // The OFF-SCREEN pane must burn NOTHING — this is the CPU fix.
                Assert.Equal(offBefore, offscreen.RebuildCount);

                // NOTHING IS LOST: the VT engine keeps consuming PTY output eagerly off-thread while the
                // pane is off screen, so a long burst of agent output is all still in the buffer.
                for (int i = 2; i <= 40; i++)
                    offFake.FireOutput($"OFFSCREEN_LINE_{i}\r\n");
                await Drain();
                TerminalControl.DeferredRendersTickForTest();
                await Drain();
                Assert.Equal(offBefore, offscreen.RebuildCount);   // still burning nothing

                // Becoming visible flushes everything that streamed while it was off screen.
                offscreen.IsVisible = true;
                await Drain();
                TerminalControl.DeferredRendersTickForTest();
                await Drain();

                Assert.True(offscreen.RebuildCount > offBefore, "becoming visible must flush the pane");
                var rendered = offscreen.RenderedText;
                Assert.Contains("OFFSCREEN_LINE", rendered);
                for (int i = 2; i <= 40; i++)
                    Assert.Contains($"OFFSCREEN_LINE_{i}", rendered);

                window.Close();
            }
            finally
            {
                TerminalControl.DeferredRendersIntervalMs = originalInterval;
            }
        });
    }

    /// <summary>
    /// Re-focusing an off-screen-then-visible pane must show the agent's CURRENT screen immediately — the
    /// operator clicks a pane precisely to read what the model rendered while they were elsewhere, so a stale
    /// or partial view is a correctness bug, not just a cosmetic one.
    /// </summary>
    [Fact]
    public Task Refocusing_flushes_everything_the_agent_rendered_while_hidden()
    {
        return _fx.DispatchAsync(async () =>
        {
            var originalInterval = TerminalControl.DeferredRendersIntervalMs;
            try
            {
                // The slow tick must never fire: this proves the FOCUS path alone does the flush.
                TerminalControl.DeferredRendersIntervalMs = 60_000;

                var term = new TerminalControl { Name = "Hidden" };
                var window = new Window { Width = 800, Height = 600, Content = term };
                window.Show();
                await Drain();

                var fake = new FakePtySession();
                term.Attach(fake);
                await Drain();

                term.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
                term.IsVisible = false;
                await Drain();

                var before = term.RebuildCount;

                // The agent works away, unobserved — a realistic burst of model output.
                for (int i = 0; i < 60; i++)
                    fake.FireOutput($"MODEL_OUTPUT_{i}\r\n");
                await Drain();
                Assert.Equal(before, term.RebuildCount);   // no work while hidden

                // Operator brings the pane back and clicks into it.
                term.IsVisible = true;
                term.RaiseEvent(new Avalonia.Input.GotFocusEventArgs());
                await Drain();

                var rendered = term.RenderedText;
                for (int i = 0; i < 60; i++)
                    Assert.Contains($"MODEL_OUTPUT_{i}", rendered);

                // The visible slice shows the newest output, not a stale frame.
                Assert.Contains("MODEL_OUTPUT_59", InlineText(ScreenOf(term)));

                window.Close();
            }
            finally
            {
                TerminalControl.DeferredRendersIntervalMs = originalInterval;
            }
        });
    }
}
