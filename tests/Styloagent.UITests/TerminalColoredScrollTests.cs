using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class TerminalColoredScrollTests(HeadlessAvaloniaFixture fixture)
{
    [Fact]
    public Task Scrolling_colored_diff_keeps_one_shaped_line_per_terminal_row()
        => fixture.DispatchAsync(async () =>
        {
            var terminal = new TerminalControl();
            var pty = new FakePtySession();
            var window = new Window { Width = 760, Height = 420, Content = terminal };
            window.Show();
            try
            {
                terminal.Attach(pty);
                for (var i = 0; i < 400; i++)
                    pty.FireOutput($"\u001b[48;2;23;43;50m\u001b[90m{i:0000} \u001b[32m+ \u001b[1;37mContent is preserved by default.\u001b[22m No route, view, export or ordinary traffic.\u001b[0m\r\n");
                await Drain();
                var screen = terminal.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "ScreenText");
                for (var step = 0; step < 25; step++)
                {
                    terminal.HandleWheelScroll(3);
                    pty.FireOutput($"\u001b[48;2;23;43;50mNEW_{step:0000}\u001b[0m\r\n");
                    await Drain();
                    var rows = screen.Inlines!.OfType<LineBreak>().Count() + 1;
                    Assert.Equal(rows, screen.TextLayout.TextLines.Count);
                    Assert.All(screen.TextLayout.TextLines, line => Assert.InRange(line.Height,
                        terminal.CellHeightForTest() - .01, terminal.CellHeightForTest() + .01));
                    Assert.InRange(screen.TextLayout.Height, rows * terminal.CellHeightForTest() - .1,
                        rows * terminal.CellHeightForTest() + .1);
                }
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task Wheel_over_mouse_tracking_TUI_is_delivered_to_the_PTY()
        => fixture.DispatchAsync(async () =>
        {
            var terminal = new TerminalControl();
            var pty = new FakePtySession();
            var window = new Window { Width = 760, Height = 420, Content = terminal };
            window.Show();
            try
            {
                terminal.Attach(pty);
                pty.FireOutput("\u001b[?1049h\u001b[?1000h\u001b[?1006h");
                await Drain();
                pty.ClearWrites();
                var screen = terminal.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "ScreenText");
                var point = new Point(12 + screen.Width / terminal.PtyCols * 6.5,
                    4 + terminal.CellHeightForTest() * 3.5);
                window.MouseWheel(point, new Vector(0, .25));
                window.MouseWheel(point, new Vector(0, .25));
                Assert.Empty(pty.Writes);
                window.MouseWheel(point, new Vector(0, .5));
                await Drain();
                Assert.Equal("\x1b[<64;7;4M", Assert.Single(pty.Writes));
                pty.ClearWrites();
                window.MouseWheel(point, new Vector(0, -2));
                await Drain();
                Assert.Equal("\x1b[<65;7;4M\x1b[<65;7;4M", Assert.Single(pty.Writes));
            }
            finally { window.Close(); }
        });

    [Fact]
    public Task Reattaching_after_replay_truncation_preserves_TUI_modes_and_hidden_redraws()
        => fixture.DispatchAsync(async () =>
        {
            var pty = new FakePtySession();
            var first = new TerminalControl();
            var window = new Window { Width = 760, Height = 420, Content = first };
            window.Show();
            try
            {
                first.Attach(pty);
                pty.FireOutput("\x1b[?1049h\x1b[?1000h\x1b[?1006h\x1b[?25l\x1b[2J\x1b[HHEADER");
                // A busy native TUI has emitted more than the 256 KiB raw replay cap.
                var frame = "\x1b[3;1H\x1b[48;2;23;43;50m0671 + Content is preserved by default.\x1b[0m";
                pty.FireOutput(string.Concat(Enumerable.Repeat(frame, 6000)));
                pty.SeedBacklog(frame); // startup modes are no longer in the raw tail
                await Drain();
                first.Detach();
                pty.FireOutput("\x1b[3;1H\x1b[2K0672 + Updated while the pane was hidden.");
                var second = new TerminalControl();
                window.Content = second;
                await Drain();
                second.Attach(pty);
                await Drain();
                Assert.True(second.IsAltBuffer, "Truncated replay lost the alternate screen initialization.");
                Assert.Contains("HEADER", second.ScreenGrid()[0]);
                Assert.Contains("0672 + Updated while the pane was hidden.", second.ScreenGrid()[2]);
                Assert.Equal(second.PtyRows, second.RenderedText.Split('\n').Length);
                pty.ClearWrites();
                second.HandleWheelScroll(1);
                Assert.Contains(pty.Writes, w => w.StartsWith("\x1b[<64;", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });

    private static async Task Drain()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
