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
}
