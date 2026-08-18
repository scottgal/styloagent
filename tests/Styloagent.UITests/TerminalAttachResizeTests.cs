using Avalonia.Controls;
using Avalonia.Threading;
using Styloagent.Terminal;

namespace Styloagent.UITests;

/// <summary>
/// The PTY winsize must match the pane the child is actually painting into.
///
/// A session is spawned at the seeded 80x24 grid BEFORE its view lays out, so by the time the pane attaches
/// the engine already holds the real (much taller) grid. A TUI that reads its size from the winsize can then
/// paint only the top ~24 rows of a tall pane forever. This is the unit-level guard for that "top-half bug".
/// </summary>
[Collection("Avalonia")]
public class TerminalAttachResizeTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TerminalAttachResizeTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static async Task DrainAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    [Fact]
    public Task Attach_pushes_the_laid_out_grid_to_a_pty_that_was_spawned_smaller()
    {
        return _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl();
            // Lay the control out TALL with no session attached — this is the real ordering: the view
            // measures (resizing the engine) while the PTY it will host has not attached yet.
            var window = new Window { Content = control, Width = 1200, Height = 1600 };
            window.Show();
            await DrainAsync();

            var laidOutRows = control.PtyRows;
            var laidOutCols = control.PtyCols;
            // Guard the premise: if the control did not lay out taller than the 24-row spawn default,
            // this test proves nothing.
            Assert.True(laidOutRows > 24,
                $"premise broken: control laid out at only {laidOutRows} rows, expected >24");

            var fake = new FakePtySession();
            control.Attach(fake);
            await DrainAsync();

            // The child was spawned at 80x24 and knows nothing of the real pane. Attach MUST assert the
            // winsize, even though the engine already matches it — otherwise the child paints 24 rows.
            Assert.True(fake.LastResize is not null,
                "attach never told the PTY its size — a TUI child stays at the 80x24 spawn grid and paints " +
                "only the top of the pane");
            Assert.Equal((laidOutCols, laidOutRows), fake.LastResize!.Value);

            window.Close();
        });
    }
}
