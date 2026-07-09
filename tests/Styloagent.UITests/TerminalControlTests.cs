using Avalonia.Controls;
using Avalonia.Threading;
using Mostlylucid.Avalonia.UITesting;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class TerminalControlTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public TerminalControlTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    /// <summary>
    /// Mounting a TerminalControl, attaching a fake session, firing output on the UI thread,
    /// and asserting the known string appears in RenderedText.
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

            await using var session = await UITestSession.AttachAsync(window, opts =>
            {
                opts.ScreenshotDir = "test-output";
                opts.Log = _ => { };
            });

            // Act: fire output — OnSessionOutput posts RebuildRows to Dispatcher.UIThread.
            // Since we're already on the UI thread inside DispatchAsync, the Post will
            // execute as soon as we yield (via InvokeAsync below).
            fake.FireOutput("HELLO_TERMINAL");

            // Drain the UI thread's Render-priority queue so RebuildRows executes.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Assert: RenderedText is read on the UI thread (we're already on it).
            Assert.Contains("HELLO_TERMINAL", control.RenderedText);

            window.Close();
        });
    }

    /// <summary>
    /// Pressing Enter on the TerminalControl forwards a CR (carriage-return) to the session.
    /// </summary>
    [Fact]
    public Task KeyPress_Enter_ForwardsCarriageReturnToSession()
    {
        return _fx.DispatchAsync(async () =>
        {
            // Arrange
            var fake = new FakePtySession();
            var control = new TerminalControl { Name = "Terminal" };
            control.Attach(fake);

            var window = new Window
            {
                Content = control,
                Width = 800,
                Height = 400
            };
            window.Show();

            await using var session = await UITestSession.AttachAsync(window, opts =>
            {
                opts.Log = _ => { };
            });

            // Act: focus the control so KeyDown events route to it, then press Enter.
            control.Focus();
            await session.PressAsync("Enter", controlName: "Terminal");

            // Assert: the fake session recorded the CR write.
            // XTerm.NET GenerateKeyInput(Key.Enter, None) returns "\r" (0x0D).
            Assert.Contains(fake.Writes, w => w.Contains('\r'));

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
}
