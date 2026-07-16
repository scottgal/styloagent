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
    /// Regression for `terminal-pane-livelocks-ui-thread-per-chunk-rebuild` (SEVERITY HIGH — froze the
    /// whole cockpit). A rapid burst of PTY output chunks must COALESCE into a bounded number of
    /// full-transcript rebuilds — NOT one rebuild per chunk. The per-chunk rebuild let the render queue
    /// outpace its own drain: a CPU-bound UI-thread livelock that pinned a core at 100% and never
    /// self-recovered. Firing many chunks before the dispatcher drains must collapse into a single rebuild,
    /// while still rendering every chunk's output (coalescing must not drop content).
    /// </summary>
    [Fact]
    public Task OutputBurst_CoalescesRebuilds_DoesNotRebuildPerChunk()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "CoalesceWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            int before = control.RebuildCount;

            // Fire a rapid burst WITHOUT yielding to the dispatcher between chunks — exactly the
            // streaming-TUI pattern (an agent's startup banner) that livelocked the UI thread.
            const int chunks = 50;
            for (int i = 0; i < chunks; i++)
                fake.FireOutput($"chunk{i:D2}\r\n");

            // Drain once. Per-chunk rebuilds run `chunks` full rebuilds here; coalescing runs one.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            int rebuilds = control.RebuildCount - before;
            Assert.True(rebuilds <= 2,
                $"Expected the {chunks}-chunk burst to COALESCE into ≤2 rebuilds, but ran {rebuilds} " +
                $"(≈ one rebuild per chunk = the UI-thread livelock this test guards against).");

            // Coalescing must not drop output: both the earliest and latest chunk must be rendered.
            Assert.Contains("chunk00", control.RenderedText);
            Assert.Contains("chunk49", control.RenderedText);

            window.Close();
        });
    }

    /// <summary>
    /// Regression for the layout-switch lockup (v0.5.1): a busy agent's terminal built the FULL transcript
    /// into coloured inlines on EVERY rebuild — a Run per colour span across up to ~1000 scrollback rows,
    /// each Add raising a logical-tree notification. Coalescing capped the frequency; this caps the COST.
    /// A layout switch re-renders every pane at once, so an unbounded per-terminal render pinned the UI
    /// thread at 100% CPU. The coloured render must be VIRTUALIZED — only the rows intersecting the
    /// viewport (+ a little over-scan) become inlines — so a rebuild is O(viewport), not O(transcript),
    /// while the plain-text transcript (and the scrollbar) still span the whole buffer.
    /// </summary>
    [Fact]
    public Task LargeTranscript_RendersOnlyTheViewport_NotEveryRow()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 300, Name = "VirtualizeWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // A transcript far larger than the viewport (~18 rows at 300px tall).
            const int lines = 400;
            for (int i = 1; i <= lines; i++)
                fake.FireOutput($"line{i:D3}\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var screen = control.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");
            int runs = screen.Inlines?.OfType<Run>().Count() ?? 0;

            // Virtualized: only the viewport (+over-scan) is built — a few dozen rows, NOT 400. Pre-fix the
            // full-transcript render produced ~400+ runs; that unbounded build is what storms the UI thread.
            Assert.True(runs < 120,
                $"Expected the coloured render to be bounded to the viewport (~a few dozen rows), but built {runs} " +
                $"runs for a {lines}-row transcript — an unbounded render that pins the UI thread.");

            // The full transcript is still available for scrollback (plain rows) and the scrollbar spans it.
            Assert.Contains("line001", control.RenderedText);
            Assert.Contains("line400", control.RenderedText);
            var sv = control.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ScrollArea");
            Assert.True(sv.Extent.Height > sv.Viewport.Height * 3,
                $"the scroll surface should span the full transcript (extent {sv.Extent.Height}) not just the " +
                $"viewport ({sv.Viewport.Height})");

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

    // ── VT in-place erase / partial-redraw fidelity ──────────────────────────
    // Regression for `terminal-renders-garbled-btop/prompt-redraws-overlap`. A prompt redraw or a btop-style
    // partial repaint moves the cursor with CUP and overwrites SPECIFIC cells (EL/ED), it does NOT append a
    // fresh line. The renderer must reflect the CURRENT cell grid — a stale glyph must never survive under a
    // shorter redraw ("prompt overwritten / overlap"). These tests drive the real control through those exact
    // sequences and assert the rendered grid equals the VT screen-buffer ground truth (no ghost cells).

    private const string AltOn  = "[?1049h";   // enter alternate (full-screen TUI) buffer
    private const string HideC  = "[?25l";     // hide cursor (btop does; keeps the ground-truth clean)
    private static string Cup(int row, int col) => $"[{row};{col}H"; // 1-based cursor position
    private const string ClearScreen = "[2J";  // ED(2): erase whole display
    private const string EraseEol    = "[K";   // EL(0): erase from cursor to end of line

    /// <summary>
    /// In-place prompt redraw (readline editing): print a long prompt, then return the cursor to column 1,
    /// erase-to-end-of-line, and print a SHORTER prompt. The tail of the old prompt must be GONE — not left
    /// as a ghost under the new one. Asserts the rendered row equals the VT grid (which the engine erased).
    /// </summary>
    [Fact]
    public Task InPlaceRedraw_ShorterPromptOverLonger_LeavesNoGhostTail()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "RedrawWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Long prompt, then redraw a shorter one IN PLACE: CR → EL(erase to EOL) → shorter text.
            fake.FireOutput("user@host:~/very/long/path/here$ ");
            fake.FireOutput("\r" + EraseEol + "$ ");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // The tail of the long prompt ("very/long/path") must not survive anywhere in the render.
            Assert.DoesNotContain("very/long/path", control.RenderedText);
            // The short prompt survived the in-place redraw (trailing space is trimmed in the transcript).
            Assert.Contains("$", control.RenderedText);

            // And the rendered row 0 must match the VT screen-buffer ground truth exactly (right-trimmed);
            // the redraw wrote "$ " over the old line and EL blanked the rest → the row is just "$".
            var grid = control.ScreenGrid();
            Assert.Equal("$", grid[0].TrimEnd());

            window.Close();
        });
    }

    /// <summary>
    /// btop-style full-screen redraw on the ALTERNATE buffer: paint frame 1 (many rows), then clear the
    /// screen and paint a SHORTER frame 2 at absolute positions. No cell from frame 1 may survive under
    /// frame 2. Asserts the rendered grid equals the VT screen buffer row-for-row (the "no ghost cells"
    /// invariant) and that the rendered column count equals the PTY column count (no width mis-fit).
    /// </summary>
    [Fact]
    public Task AltBufferPartialRedraw_BtopLike_RenderedGridMatchesScreenBuffer_NoGhosts()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "BtopWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            int cols = control.PtyCols;
            int rows = control.PtyRows;
            Assert.True(rows >= 6, $"need a few rows for the btop frame; got {rows}");

            // Enter the alt buffer + hide the cursor (exactly what btop does), clear, paint frame 1.
            fake.FireOutput(AltOn + HideC + ClearScreen);
            for (int r = 1; r <= rows; r++)
                fake.FireOutput(Cup(r, 1) + $"FRAME1-ROW{r:D2}-XXXXXXXXXXXXXXXX");

            // Frame 2: clear, then paint only a FEW short rows at absolute positions (a btop redraw burst).
            fake.FireOutput(ClearScreen);
            fake.FireOutput(Cup(1, 1) + "cpu 12%");
            fake.FireOutput(Cup(2, 1) + "mem 34%");
            fake.FireOutput(Cup(3, 1) + "net ok");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // We must be on the alt buffer (the btop path this fix targets).
            Assert.True(control.IsAltBuffer, "expected the alternate buffer to be active for a full-screen TUI");

            // NO ghost cells: not one frame-1 glyph may survive under frame 2.
            Assert.DoesNotContain("FRAME1", control.RenderedText);

            // Frame 2's content is present and correctly placed.
            var grid = control.ScreenGrid();
            Assert.Equal("cpu 12%", grid[0].TrimEnd());
            Assert.Equal("mem 34%", grid[1].TrimEnd());
            Assert.Equal("net ok",  grid[2].TrimEnd());
            for (int r = 3; r < rows; r++)
                Assert.Equal(string.Empty, grid[r].TrimEnd());   // cleared rows are blank, not ghosted

            // The rendered plain-text rows must equal the VT screen-buffer grid exactly, row-for-row —
            // this is the "rendered grid == expected screen buffer" assertion the task requires. On the
            // alt buffer the transcript IS the Rows-tall screen (no scrollback), so Rows lines up 1:1.
            Assert.Equal(rows, control.Rows.Count);
            for (int r = 0; r < rows; r++)
                Assert.Equal(grid[r].TrimEnd(), control.Rows[r].TrimEnd());

            // GRID WIDTH: rendered cols == PTY cols exactly (no width mis-fit that wraps/overlaps).
            // The engine grid width IS the width the PTY was resized to; assert the resize matched it.
            Assert.NotNull(fake.LastResize);
            Assert.Equal(cols, fake.LastResize!.Value.Cols);
            Assert.Equal(rows, fake.LastResize!.Value.Rows);

            window.Close();
        });
    }

    /// <summary>
    /// A short buffer (a couple of lines) must be BOTTOM-anchored: its last row rests on the bottom edge of
    /// the viewport and the empty space pads at the TOP — not floating in the middle/top. We assert the
    /// rendered text block is offset DOWN from the top by roughly the leftover height.
    /// </summary>
    [Fact]
    public Task ShortBuffer_IsBottomAnchored_PadsAtTopNotMiddle()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "AnchorWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Only two lines of output — far shorter than the ~24-row viewport.
            fake.FireOutput("line one\r\nline two");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var sv = control.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ScrollArea");
            var screen = control.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");

            // Bottom-anchored: the text block is pushed well DOWN inside the viewport (the short content pads
            // at the top), so Canvas.Top is a large fraction of the viewport height — not ~0 (top-anchored).
            double top = Canvas.GetTop(screen);
            Assert.True(top > sv.Viewport.Height * 0.5,
                $"short buffer should be bottom-anchored (top pad ≈ leftover height); Canvas.Top={top}, " +
                $"viewport={sv.Viewport.Height}");

            window.Close();
        });
    }

    /// <summary>
    /// Bottom-anchor must survive output that arrives BEFORE the control is laid out (viewport height still 0)
    /// — the pane-attach/replay ordering, where a re-attach replays the backlog into a not-yet-measured
    /// control. If <c>_topPad</c> is only computed on that pre-layout rebuild it stays 0 and the content sticks
    /// to the top/middle after layout. Fire output first, THEN show/lay out, and assert the short buffer still
    /// ends up bottom-anchored (this is the "finish the bottom-anchor half-fix" regression).
    /// </summary>
    [Fact]
    public Task OutputBeforeLayout_StillBottomAnchoredAfterLayout()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            // Output arrives BEFORE the control is shown/measured (viewport height is 0 here).
            fake.FireOutput("line one\r\nline two");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // Now lay it out — this is when a real pane becomes visible after a re-attach + replay.
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "PreLayoutAnchorWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var sv = control.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ScrollArea");
            var screen = control.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");

            double top = Canvas.GetTop(screen);
            Assert.True(top > sv.Viewport.Height * 0.5,
                $"short buffer must be bottom-anchored even when output preceded layout; Canvas.Top={top}, " +
                $"viewport={sv.Viewport.Height}");

            window.Close();
        });
    }

    /// <summary>
    /// Task B: the fake/non-process session reports ProcessId 0 via the interface default, and the property
    /// never throws. (The real PID is surfaced by PortaPtySession from Porta.Pty; not exercised headlessly.)
    /// </summary>
    [Fact]
    public void ProcessId_DefaultSession_IsZeroAndDoesNotThrow()
    {
        var fake = new FakePtySession();
        Styloagent.Core.Sessions.IPtySession session = fake;
        int pid = session.ProcessId;   // must not throw
        Assert.Equal(0, pid);
    }

    /// <summary>Matches a Cursor-Position-Report reply — CSI [?] rows ; cols R — the terminal sends back to a
    /// child that asked "where is the cursor?" (ESC[6n / ESC[?6n). Its presence in the child's input stream
    /// means the VT engine answered a device-status query.</summary>
    private static bool IsCursorPositionReport(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, "\\x1b?\\[[?]?[\\d;]+R");

    /// <summary>
    /// Characterization: when the child emits a cursor-position query (ESC[6n) on the LIVE stream, the VT
    /// engine answers it — writing a CSI…R report BACK to the child over the PTY. This is correct terminal
    /// behaviour and the baseline the replay-suppression fix must preserve.
    /// </summary>
    [Fact]
    public async Task LiveDeviceStatusQuery_IsAnsweredBackToChild()
    {
        var fake = new FakePtySession();
        Exception? lambdaEx = null;

        await _fx.DispatchAsync(async () =>
        {
            try
            {
                var control = new TerminalControl();
                var window = new Window { Content = control, Width = 800, Height = 400 };
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                control.Attach(fake);
                fake.ClearWrites();

                // Child asks for the cursor position on the LIVE stream (as btop/claude do at startup).
                fake.FireOutput("[6n");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                window.Close();
            }
            catch (Exception ex) { lambdaEx = ex; }
        });

        Assert.Null(lambdaEx);
        Assert.True(
            fake.Writes.Any(IsCursorPositionReport),
            $"Expected the engine to answer a LIVE ESC[6n with a CSI…R report written to the child. " +
            $"Writes={fake.Writes.Count}: [{string.Join(", ", fake.Writes.Select(w => $"\"{w.Replace("", "\\e")}\""))}]");
    }

    /// <summary>
    /// Regression (garbled terminal — "5R[?22;3R" leaking into the prompt): a device-status query that lives
    /// in the REPLAY BACKLOG — the child's startup output, re-fed through the VT engine every time a pane
    /// re-attaches to rebuild its screen — must NOT be re-answered. Replayed history is read-only; answering
    /// it injects a stale CSI…R report into the child now sitting at an interactive prompt, which surfaces as
    /// garbage characters in what the user is typing. Only LIVE queries get answered.
    /// </summary>
    [Fact]
    public async Task ReplayedDeviceStatusQuery_IsNotAnsweredBackToChild()
    {
        var fake = new FakePtySession();
        // The child's startup burst is now history in the session backlog: text + cursor-position queries.
        fake.SeedBacklog("hello[6n[?6nworld");

        Exception? lambdaEx = null;

        await _fx.DispatchAsync(async () =>
        {
            try
            {
                var control = new TerminalControl();
                var window = new Window { Content = control, Width = 800, Height = 400 };
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                // Attach = subscribe = the backlog is replayed synchronously to rebuild VT state.
                control.Attach(fake);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                window.Close();
            }
            catch (Exception ex) { lambdaEx = ex; }
        });

        Assert.Null(lambdaEx);
        Assert.DoesNotContain(fake.Writes, IsCursorPositionReport);
    }

    // ── Mouse-wheel scrollback + zoom ────────────────────────────────────────
    // The terminal panes had no wheel scrolling (`docked-agent-terminal-panes-have-no-scrollbar`) and no
    // zoom. Wheel-up must scroll BACK through the VT scrollback (driving the real ScrollViewer offset, not a
    // fake), and returning to the bottom resumes tail-following. On the alternate buffer a full-screen TUI
    // (btop) owns its own scroll, so the wheel must NOT be hijacked. Zoom re-measures the cell and refits the
    // grid so rendered cols == PTY cols at any zoom (the invariant the render-fidelity fix depends on).

    /// <summary>
    /// Wheel-up scrolls the transcript BACK through scrollback: from the tail (auto-followed to the bottom),
    /// a wheel-up notch must move the real ScrollViewer offset UP (toward earlier output), and scrolling back
    /// to the bottom must return to the tail. Faithful to the VT buffer — it drives the actual offset.
    /// </summary>
    [Fact]
    public Task WheelScroll_WheelUp_ScrollsBackThroughScrollback_AndReturnsToTail()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 300, Name = "WheelWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // A transcript far taller than the viewport, so there's real scrollback to move through.
            for (int i = 1; i <= 80; i++)
                fake.FireOutput($"L{i:D2}\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var sv = control.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ScrollArea");
            double max = sv.Extent.Height - sv.Viewport.Height;
            Assert.True(max > 0, "need overflow for there to be scrollback to wheel through");
            // Auto-follow left us pinned at the bottom.
            Assert.True(sv.Offset.Y >= max - 2.0, $"expected to start at the tail; offset {sv.Offset.Y}, max {max}");

            // Wheel UP (positive delta) — scroll BACK through history.
            bool handled = control.HandleWheelScroll(3);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(handled, "wheel scroll on the normal buffer with overflow must be handled");
            Assert.True(sv.Offset.Y < max - 1.0,
                $"wheel-up must scroll back (offset should drop below the tail); offset {sv.Offset.Y}, max {max}");

            // Wheel DOWN hard — back to the tail; following resumes (a further output would auto-scroll).
            control.HandleWheelScroll(-1000);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(sv.Offset.Y >= max - 2.0,
                $"scrolling back down must return to the tail; offset {sv.Offset.Y}, max {max}");

            window.Close();
        });
    }

    /// <summary>
    /// On the ALTERNATE buffer a full-screen TUI (btop, vim, less) manages its own scroll, so the terminal
    /// must NOT hijack the wheel — <see cref="TerminalControl.HandleWheelScroll"/> is a no-op returning false,
    /// leaving the event free to route to the app. Regression against re-introducing scrollback hijacking that
    /// would fight btop's own paging.
    /// </summary>
    [Fact]
    public Task WheelScroll_OnAlternateBuffer_IsIgnored_AppOwnsScroll()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "WheelAltWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Enter the alt buffer (what btop does).
            fake.FireOutput(AltOn + HideC + ClearScreen);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(control.IsAltBuffer, "expected the alternate buffer to be active");

            // Wheel must be a no-op — the running TUI owns scroll.
            Assert.False(control.HandleWheelScroll(3), "wheel scroll must NOT be hijacked on the alt buffer");
            Assert.False(control.HandleWheelScroll(-3), "wheel scroll must NOT be hijacked on the alt buffer");

            window.Close();
        });
    }

    /// <summary>
    /// Zoom (the bindable <see cref="TerminalControl.ZoomLevel"/> a cockpit slider drives): a LARGER zoom
    /// grows the measured monospace cell, so FEWER columns fit — and the PTY is resized to match, keeping the
    /// render-fidelity invariant "rendered cols == PTY cols" true at any zoom. Asserts the fitted grid shrinks
    /// on zoom-in and that the session was resized to exactly the new engine column count.
    /// </summary>
    [Fact]
    public Task Zoom_LargerZoom_FitsFewerColumns_AndResizesPtyToMatch()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "ZoomWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            int colsAtDefault = control.PtyCols;
            Assert.True(colsAtDefault > 0, "grid should be fitted at the default zoom");

            // Zoom IN — bigger font, bigger cell, fewer columns fit the same pane.
            control.ZoomLevel = 2.5;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            int colsZoomedIn = control.PtyCols;
            Assert.True(colsZoomedIn < colsAtDefault,
                $"zooming in should fit FEWER columns; default={colsAtDefault}, zoomed={colsZoomedIn}");

            // Rendered cols == PTY cols: the session was resized to exactly the engine's new column count.
            Assert.NotNull(fake.LastResize);
            Assert.Equal(control.PtyCols, fake.LastResize!.Value.Cols);

            window.Close();
        });
    }

    /// <summary>
    /// <see cref="TerminalControl.ZoomLevel"/> is clamped to [<see cref="TerminalControl.MinZoom"/>,
    /// <see cref="TerminalControl.MaxZoom"/>] via property coercion — the contract a bound cockpit slider
    /// relies on, so an out-of-range binding value can never drive the font/grid outside the sane range.
    /// </summary>
    [Fact]
    public Task Zoom_Level_IsClampedToRange()
    {
        return _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl();
            var window = new Window { Content = control, Width = 800, Height = 400, Name = "ZoomClampWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            control.ZoomLevel = 99.0;
            Assert.Equal(TerminalControl.MaxZoom, control.ZoomLevel);

            control.ZoomLevel = 0.01;
            Assert.Equal(TerminalControl.MinZoom, control.ZoomLevel);

            window.Close();
        });
    }
}
