using Avalonia.Controls;
using Avalonia.Threading;
using Mostlylucid.Avalonia.UITesting.Players;
using SkiaSharp;
using Styloagent.Terminal;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class TerminalScreenshotTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TerminalScreenshotTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    // Renders the TerminalControl with known text to a real PNG (via the UITesting framework's
    // ScreenshotCapture) and asserts the pixels contain VISIBLE (bright) text — so an "invisible
    // terminal" (text = background, or nothing rendered) can never pass silently again.
    [Fact]
    public async Task Terminal_renders_visible_bright_text()
    {
        const string path = "/tmp/styloagent-terminal.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        await _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl { Width = 620, Height = 200 };
            var fake = new FakePtySession();
            var window = new Window { Width = 640, Height = 220, Content = control };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            control.Attach(fake);
            fake.FireOutput("STYLOAGENT TERMINAL\r\n> hello world 123\r\n 1. Yes, I trust this folder\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ScreenshotCapture.CaptureControlAsync(window, control, path);
            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "screenshot PNG should be written");

        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        int bright = 0;
        for (int y = 0; y < bmp!.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Red > 120 && p.Green > 120 && p.Blue > 120) bright++;
        }
        // Text is drawn in #EDEDED on #0C0C0C — visible text produces thousands of bright pixels;
        // an all-dark (invisible) terminal produces ~none.
        Assert.True(bright > 300, $"Terminal should render visible bright text pixels, but found only {bright}.");
    }

    // Feeds ANSI-coloured output and asserts the rendered pixels contain DISTINCT red and green
    // (not just monochrome brightness) — so a regression back to monochrome can't pass silently.
    [Fact]
    public async Task Terminal_renders_ansi_colours()
    {
        const string path = "/tmp/styloagent-terminal-colour.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        await _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl { Width = 620, Height = 200 };
            var fake = new FakePtySession();
            var window = new Window { Width = 640, Height = 220, Content = control };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            control.Attach(fake);
            // 31m = ANSI red, 32m = ANSI green, 38;2 = 24-bit truecolor orange.
            fake.FireOutput("[31mRRRRRRRR[0m\r\n[32mGGGGGGGG[0m\r\n"
                          + "[38;2;215;119;87mOOOOOOOO[0m\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ScreenshotCapture.CaptureControlAsync(window, control, path);
            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "colour screenshot PNG should be written");

        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        int redPixels = 0, greenPixels = 0;
        for (int y = 0; y < bmp!.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Red > 130 && p.Green < 90 && p.Blue < 90) redPixels++;
            if (p.Green > 130 && p.Red < 90 && p.Blue < 90) greenPixels++;
        }
        Assert.True(redPixels > 50, $"Expected red-dominant pixels from \\e[31m text, found {redPixels}.");
        Assert.True(greenPixels > 50, $"Expected green-dominant pixels from \\e[32m text, found {greenPixels}.");
    }

    // Feeds a coloured BACKGROUND and INVERSE-video output and asserts filled colour blocks render
    // (not just coloured glyphs) — so per-cell background + inverse can't silently regress.
    [Fact]
    public async Task Terminal_renders_background_and_inverse()
    {
        const string path = "/tmp/styloagent-terminal-bg.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        await _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl { Width = 620, Height = 200 };
            var fake = new FakePtySession();
            var window = new Window { Width = 640, Height = 220, Content = control };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            control.Attach(fake);
            // 44m = blue background; 7m = inverse video over default fg/bg (light block).
            fake.FireOutput("\u001b[44m        \u001b[0m\r\n\u001b[7m        \u001b[0m\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ScreenshotCapture.CaptureControlAsync(window, control, path);
            window.Close();
        });

        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        int blueBlock = 0, lightBlock = 0;
        for (int y = 0; y < bmp!.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            // Blue background block (ANSI 4 = ~#0000EE): strong blue, low red/green.
            if (p.Blue > 120 && p.Red < 90 && p.Green < 90) blueBlock++;
            // Inverse of default (light-on-dark) fills the cell with the light default fg (#EDEDED).
            if (p.Red > 180 && p.Green > 180 && p.Blue > 180) lightBlock++;
        }
        Assert.True(blueBlock > 200, $"Expected a filled blue background block from \\e[44m, found {blueBlock}.");
        Assert.True(lightBlock > 200, $"Expected a filled light block from inverse-video \\e[7m, found {lightBlock}.");
    }

    // Visual proof for `terminal-renders-garbled-btop/prompt-redraws-overlap`: replays a REAL btop capture
    // (assets/btop-capture.raw — 57 KB of genuine btop output: alt-buffer, absolute cursor addressing and
    // ~3090 partial-redraw CSI sequences, the exact stress the issue names) through the actual TerminalControl
    // and renders it to a PNG. The capture was taken at 118x39 — the EXACT grid this pane fits at 940x600 — so
    // btop fills the pane edge-to-edge and its bottom status row hugs the bottom edge (suspect 3). Proves
    // against LIVE btop bytes (not a synthetic burst) that the engine stays on the alt buffer, rendered cols ==
    // PTY cols (no grid-width mis-fit), the bottom row is real content on the edge (not blank/floating), and a
    // rich, colourful frame paints — i.e. the redraws did NOT collapse into garbage/overlap. The pixel-exact
    // "no ghost cells" invariant is asserted separately/synthetically by AltBufferPartialRedraw_BtopLike... .
    [Fact]
    public async Task RealBtopCapture_RendersRichAltBufferFrame_ColsMatchPty()
    {
        var asset = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "btop-capture.raw");
        Assert.True(System.IO.File.Exists(asset), $"btop capture asset missing at {asset}");
        // Real btop output is UTF-8 (box-drawing/braille graph glyphs); decode it the way the PTY would.
        string btop = await System.IO.File.ReadAllTextAsync(asset, System.Text.Encoding.UTF8);

        const string path = "/tmp/styloagent-btop-live.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        int cols = 0, rows = 0, renderedRowCount = 0;
        bool altBuffer = false;
        string lastRow = "";
        (int Cols, int Rows)? lastResize = null;

        await _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);   // attach BEFORE Show so the initial layout refit resizes the session too
            // 940x600 fits exactly the 118x39 grid the capture was taken at, so btop fills the pane edge-to-edge
            // (no blank surplus rows/cols) and its bottom status row rests on the bottom edge.
            var window = new Window { Width = 940, Height = 600, Content = control, Name = "BtopLiveWindow" };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Replay the whole real btop session through the render path.
            fake.FireOutput(btop);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            altBuffer = control.IsAltBuffer;
            cols = control.PtyCols;
            rows = control.PtyRows;
            renderedRowCount = control.Rows.Count;
            lastRow = control.Rows.Count > 0 ? control.Rows[^1] : "";
            lastResize = fake.LastResize;

            await ScreenshotCapture.CaptureControlAsync(window, control, path);
            window.Close();
        });

        // btop is a full-screen TUI: it must be on the alternate buffer (the render path the fix targets).
        Assert.True(altBuffer, "expected btop to be rendered on the alternate buffer");
        // GRID-WIDTH FIT: rendered cols == PTY cols. The engine grid width IS what the PTY was resized to.
        Assert.NotNull(lastResize);
        Assert.Equal(cols, lastResize!.Value.Cols);
        Assert.Equal(rows, lastResize!.Value.Rows);
        // The capture was taken at 118x39; the pane must be at least that big so the frame isn't clipped.
        Assert.True(cols >= 118, $"pane should be wide enough for the 118-col btop frame; got {cols} cols");
        Assert.True(rows >= 39, $"pane should be tall enough for the 39-row btop frame; got {rows} rows");

        // BOTTOM-ANCHOR (suspect 3): on the alt buffer the transcript IS the Rows-tall screen, and btop paints
        // its footer/status line on the LAST row — so the bottom rendered row must be real content resting on
        // the bottom edge, NOT blank filler and NOT floating mid-pane. Rows lines up 1:1 with the PTY grid.
        Assert.Equal(rows, renderedRowCount);
        Assert.False(string.IsNullOrWhiteSpace(lastRow),
            $"btop's bottom status row should hug the bottom edge with real content, but the last rendered row " +
            $"was blank ('{lastRow}') — content is floating/short-padded instead of bottom-filled");

        Assert.True(System.IO.File.Exists(path), "btop screenshot PNG should be written");
        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        int bright = 0, colourful = 0;
        for (int y = 0; y < bmp!.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var p = bmp.GetPixel(x, y);
            int max = Math.Max(p.Red, Math.Max(p.Green, p.Blue));
            int min = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
            if (max > 120) bright++;              // rendered glyphs/graphs (not an all-dark blank pane)
            if (max - min > 45 && max > 90) colourful++;  // btop's coloured meters/graphs — proves colour paints
        }
        // A real btop frame fills the pane with thousands of bright glyph pixels AND distinctly coloured
        // meter/graph pixels. A garbled/overlapping/blank render would fail one of these.
        Assert.True(bright > 3000, $"expected a richly-rendered btop frame (many bright pixels), found {bright}");
        Assert.True(colourful > 500, $"expected btop's coloured meters/graphs to render, found {colourful} colour pixels");
    }
}
