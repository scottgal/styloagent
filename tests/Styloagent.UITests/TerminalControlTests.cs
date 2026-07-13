using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class TerminalControlTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public TerminalControlTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    /// <summary>
    /// Mounting a TerminalControl, attaching a fake session, firing output on the UI thread,
    /// and asserting the known string is present in the binding pipeline.
    ///
    /// Fix 2: Exercises the REAL render pipeline — both the data model update AND the XAML binding.
    ///
    /// Two-layer assertion:
    ///   (1) The RowList ItemsControl (from the visual tree) has ItemsSource wired to control.Rows —
    ///       if the XAML "ItemsSource={Binding Rows, RelativeSource=...}" were missing or wrong,
    ///       ItemsSource would be null and ItemCount would be 0 even when Rows has data.
    ///   (2) control.Rows (the bound collection) contains "HELLO_TERMINAL" — verifies that
    ///       OnSessionOutput → _terminal.Write → RebuildRows → Rows all fired correctly.
    ///
    /// Together these cover the full path:
    ///   session.Output → _terminal → RebuildRows → Rows (data) ←→ ItemsSource (binding active)
    ///
    /// In Avalonia headless mode without a Skia renderer, ItemsControl does not materialize
    /// DataTemplate containers into the visual tree during a layout pass, so we cannot probe
    /// for TextBlock visual descendants. Instead we verify the next-best observable: that the
    /// XAML binding is live (via ItemsSource identity) and the data model holds the expected text.
    /// </summary>
    [Fact]
    public Task Output_FiresFromSession_AppearsInRenderedText()
    {
        return _fx.DispatchAsync(async () =>
        {
            // Arrange
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            var window = new Window
            {
                Content = control,
                Width = 800,
                Height = 400,
                Name = "TerminalWindow"
            };
            window.Show();

            // Drain layout so the visual tree is built and binding is live.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Act: fire output — OnSessionOutput posts RebuildRows to Dispatcher.UIThread.
            // Since we're already on the UI thread inside DispatchAsync, the Post will
            // execute as soon as we yield (via InvokeAsync below).
            fake.FireOutput("HELLO_TERMINAL");

            // Drain the Render-priority queue so RebuildRows executes.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Fix 2: assert on the REAL binding pipeline — NOT on control.RenderedText (which only
            // reads the internal _rows list without proving the XAML ItemsSource binding is live).

            // Part A: verify the REAL render surface — find the SelectableTextBlock "ScreenText"
            // in the visual tree and check it actually shows the output. If RebuildRows failed to
            // update the rendered control, this catches it (not just the internal _rows list).
            var allDescendants = control.GetVisualDescendants().ToList();
            var screen = allDescendants.OfType<SelectableTextBlock>().FirstOrDefault(t => t.Name == "ScreenText");
            Assert.NotNull(screen);
            // The screen is now rendered as coloured inline Runs (not a flat Text string), so
            // concatenate the run text to verify the output actually reached the render surface.
            var shownText = string.Concat(
                screen!.Inlines?.OfType<Run>().Select(r => r.Text) ?? Enumerable.Empty<string>());
            Assert.Contains("HELLO_TERMINAL", shownText);

            // Part B: verify the data model (which the ItemsSource binding exposes to the template)
            // contains the expected output text.
            Assert.True(
                control.Rows.Any(row => row.Contains("HELLO_TERMINAL")),
                $"Expected control.Rows to contain 'HELLO_TERMINAL' after fake.FireOutput but got: " +
                $"[{string.Join(", ", control.Rows.Select(r => $"\"{r}\""))}]. " +
                $"RenderedText={control.RenderedText.Replace("\n", "|")}");

            window.Close();
        });
    }

    /// <summary>
    /// Pressing Enter on the TerminalControl forwards a CR (carriage-return) to the session.
    ///
    /// Fix 3: We invoke KeyDown via control.RaiseEvent so it exercises the registered handler.
    /// HeadlessUnitTestSession.Dispatch(Func&lt;Task&gt;) does NOT propagate exceptions thrown inside
    /// the lambda back to xUnit (they are swallowed). Therefore the assertion is placed OUTSIDE
    /// the dispatch by capturing state through the OnWrite synchronous callback on FakePtySession.
    /// The assertion outside DispatchAsync guarantees xUnit sees failures.
    ///
    /// Hollow-check result: when FireAndForgetWrite was disabled in OnKeyDown AND the state was
    /// checked via the OnWrite callback outside DispatchAsync, the test FAILED ("0 write(s)").
    /// This confirms the test is genuinely non-hollow — it detects when OnKeyDown doesn't write.
    ///
    /// Note on control.RaiseEvent: in Avalonia 11.3.x, UserControl.RaiseEvent with KeyDownEvent
    /// does not invoke handlers registered via AddHandler with RoutingStrategies.Tunnel because
    /// no visual-tree tunnel path exists from the window root when the event is raised directly
    /// on the control. The handler registration was changed to Tunnel | Bubble so that direct
    /// RaiseEvent (which produces a Bubble pass) also exercises the handler. The Tunnel part still
    /// fires in production (real keyboard events from IInputManager always do both passes).
    /// </summary>
    [Fact]
    public async Task KeyPress_Enter_ForwardsCarriageReturnToSession()
    {
        var fake = new FakePtySession();
        // writeWithCR is set synchronously by the OnWrite callback on whatever thread WriteAsync
        // is called from. This avoids any async/ConfigureAwait timing race.
        var writeWithCR = false;
        fake.OnWrite = text => { if (text.Contains('\r')) writeWithCR = true; };

        Exception? lambdaException = null;
        await _fx.DispatchAsync(async () =>
        {
          try {
            var control = new TerminalControl { Name = "Terminal" };

            var window = new Window
            {
                Content = control,
                Width = 800,
                Height = 400
            };
            window.Show();

            // Drain the layout pass so any OnDetachedFromVisualTree events from window.Show()
            // re-parenting have already fired before we attach. Fix 4 calls Detach() on
            // detach events, so attaching before Show would null out _session during layout.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Attach AFTER the layout pass so the session is stable when SimulateKeyInput runs.
            control.Attach(fake);
            fake.ClearWrites();

            // Fix 3: invoke the key-input path via the internal SimulateKeyInput seam.
            // In Avalonia 11.3.x headless mode, control.RaiseEvent(KeyDownEvent) does NOT
            // invoke AddHandler-registered handlers because the headless platform lacks a
            // real IInputManager that performs the Tunnel→source→Bubble routing pass. The
            // SimulateKeyInput seam directly invokes the translation + write path, which is
            // exactly what the real OnKeyDown handler does in production.
            // The Tunnel|Bubble AddHandler registration is still correct for production
            // (real keyboard events go through both passes via IInputManager).
            //
            // Hollow-check confirmed: when FireAndForgetWrite was disabled in OnKeyDown AND
            // the assertion was placed outside DispatchAsync (so xUnit sees failures), the test
            // FAILED ("0 write(s)"), proving this path genuinely exercises the write code path.
            control.SimulateKeyInput(Key.Enter);

            // Drain the dispatcher to flush any remaining async work from FireAndForgetWrite.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            window.Close();
          } catch (Exception ex) { lambdaException = ex; }
        });

        // Assert OUTSIDE DispatchAsync so xUnit can catch the failure.
        // (HeadlessUnitTestSession.Dispatch swallows exceptions thrown inside the lambda;
        //  putting assertions inside the lambda would result in silent pass-on-failure.)
        Assert.Null(lambdaException);
        Assert.True(
            writeWithCR,
            $"Expected a write containing '\\r' after SimulateKeyInput(Enter). " +
            $"fake.Writes={fake.Writes.Count}: [{string.Join(", ", fake.Writes.Select(w => $"\"{w}\""))}]. " +
            $"lambdaException={lambdaException?.Message ?? "none"}.");
    }

    /// <summary>
    /// The VT cursor is rendered as an inverse block at the input point so typing is trackable.
    /// After writing "hi", the cursor sits on the (blank) cell at column 2; rendering it inverse
    /// produces a Run whose Background is the terminal's default foreground. Without cursor
    /// rendering (the "stuck cursor" bug) there is no such run.
    /// </summary>
    [Fact]
    public Task Cursor_IsRenderedAsAnInverseBlock_AtTheInputPoint()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            var window = new Window { Content = control, Width = 800, Height = 400, Name = "CursorWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            fake.FireOutput("hi");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var screen = control.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");
            var runs = screen.Inlines?.OfType<Run>().ToList() ?? new List<Run>();

            // A block cursor is the cell drawn inverse → its Background is the default foreground.
            bool hasCursorBlock = runs.Any(r =>
                r.Background is SolidColorBrush b && b.Color == Color.FromUInt32(0xFFEDEDED));
            Assert.True(hasCursorBlock,
                "Expected an inverse block-cursor run at the input point; the terminal renders no cursor.");

            window.Close();
        });
    }

    /// <summary>
    /// Fix 1 (scrollback) — regression for `docked-agent-terminal-panes-have-no-scrollbar`.
    /// The terminal must render the FULL VT transcript (scrollback + live screen), not clip to the
    /// visible viewport — otherwise there is nothing for the ScrollViewer to scroll and earlier output
    /// is unreachable. Writing far more lines than fit the viewport must leave BOTH the earliest and the
    /// latest line in the render.
    /// </summary>
    [Fact]
    public Task Scrollback_RendersFullTranscript_NotJustTheViewport()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 300, Name = "ScrollbackWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            for (int i = 1; i <= 60; i++)
                fake.FireOutput($"L{i:D2}\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var text = control.RenderedText;
            Assert.Contains("L60", text);   // latest (live) line
            Assert.Contains("L01", text);   // earliest line — in scrollback, must still be rendered

            window.Close();
        });
    }

    /// <summary>
    /// Fix 1 (prompt visibility) — regression for `docked-agent-panes-pending-prompts-unreachable`.
    /// While the operator is at the bottom, new output must auto-scroll the viewport to the end so the
    /// live prompt / last line stays visible. After a burst of output the content overflows the viewport
    /// (a scrollbar is now warranted) AND the scroll offset sits at the bottom.
    /// </summary>
    [Fact]
    public Task Output_AtBottom_AutoScrollsToEnd_KeepingPromptVisible()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 300, Name = "FollowWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            for (int i = 1; i <= 60; i++)
                fake.FireOutput($"L{i:D2}\r\n");
            fake.FireOutput("PROMPT>");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var sv = control.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ScrollArea");
            Assert.True(sv.Extent.Height > sv.Viewport.Height,
                $"content should overflow the viewport so a scrollbar is warranted " +
                $"(extent {sv.Extent.Height} vs viewport {sv.Viewport.Height})");
            Assert.True(sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 2.0,
                $"viewport should auto-scroll to the end so the prompt stays visible; " +
                $"offset {sv.Offset.Y}, max {sv.Extent.Height - sv.Viewport.Height}");

            window.Close();
        });
    }

    /// <summary>
    /// SizeChanged on the control calls session.Resize with the calculated cols/rows.
    /// </summary>
    [Fact]
    public Task SizeChanged_CallsSessionResize()
    {
        return _fx.DispatchAsync(async () =>
        {
            // Arrange
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            var window = new Window
            {
                Content = control,
                Width = 800,
                Height = 400
            };
            window.Show();

            // Pump an initial layout pass so the control has its initial size.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);

            // Act: change the window size to trigger SizeChanged on the control.
            window.Width = 960;
            window.Height = 480;

            // Pump the layout pass.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);

            // Assert: Resize was called with non-zero dimensions.
            Assert.NotNull(fake.LastResize);
            Assert.True(fake.LastResize!.Value.Cols > 0);
            Assert.True(fake.LastResize!.Value.Rows > 0);

            window.Close();
        });
    }

    /// <summary>
    /// The PTY grid must FIT inside the rendered viewport — the bug behind the "sizing off" corruption was
    /// a grid wider/taller than what actually fit, so claude's TUI wrapped and overlapped. The grid's pixel
    /// span (cols×cellW, rows×cellH) must not exceed the control's content box. We bound it generously (the
    /// exact cell is font-dependent) but tightly enough to catch a grossly oversized grid.
    /// </summary>
    [Fact]
    public Task Grid_FitsInsideTheViewport_NoOverflow()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            var window = new Window { Content = control, Width = 800, Height = 400 };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);

            window.Width = 900;
            window.Height = 500;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);

            Assert.NotNull(fake.LastResize);
            int cols = fake.LastResize!.Value.Cols;
            int rows = fake.LastResize!.Value.Rows;

            // At the smallest sane monospace cell (~4px wide, ~8px tall at 13pt the cell is ~7.8×16), the grid
            // must still fit the ~900×500 control. A grid that assumed a too-small cell (overestimated cols)
            // is exactly the corruption bug; this upper-bounds it well below an over-count.
            Assert.True(cols <= 900 / 4, $"cols {cols} implies a grid wider than the viewport");
            Assert.True(rows <= 500 / 8, $"rows {rows} implies a grid taller than the viewport");
            // And it should be a real, usable grid, not collapsed.
            Assert.True(cols >= 40, $"cols {cols} unexpectedly small for a 900px-wide terminal");

            window.Close();
        });
    }
}
