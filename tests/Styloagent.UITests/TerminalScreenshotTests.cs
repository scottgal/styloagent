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
}
