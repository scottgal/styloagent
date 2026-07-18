using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Terminal;

namespace Styloagent.UITests;

/// <summary>
/// Standard clipboard shortcuts in the terminal (operator ask): mac Cmd+C/X/V copy/cut/paste against the
/// system clipboard, WITHOUT shadowing the PTY's Ctrl+C=ETX / Ctrl+D=EOT. These drive the internal
/// copy/paste seams (the exact Cmd chord dispatch in OnKeyDown is manual/restart-verified — headless key
/// routing doesn't carry the Meta modifier reliably). Paste is bracketed-paste-aware.
/// </summary>
[Collection("Avalonia")]
public class TerminalClipboardTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TerminalClipboardTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static async Task DrainAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    /// <summary>Paste sends the raw text to the PTY when the child has NOT enabled bracketed-paste mode.</summary>
    [Fact]
    public Task Paste_Raw_WritesTextToPty()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            control.WritePaste("echo hello");
            await DrainAsync();

            Assert.Contains("echo hello", fake.Writes);
            // Raw paste: no bracketed-paste wrapper.
            Assert.DoesNotContain(fake.Writes, w => w.Contains("\u001b[200~"));
        });
    }

    /// <summary>
    /// When the child enabled bracketed-paste mode (DECSET 2004), paste is wrapped in ESC[200~ … ESC[201~ so
    /// a multi-line paste is treated as literal data instead of auto-executing line by line.
    /// </summary>
    [Fact]
    public Task Paste_Bracketed_WhenChildEnabledIt()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            // The child turns on bracketed-paste mode.
            fake.FireOutput("\u001b[?2004h");
            await DrainAsync();

            control.WritePaste("line1\nline2");
            await DrainAsync();

            Assert.Contains("\u001b[200~line1\nline2\u001b[201~", fake.Writes);
        });
    }

    /// <summary>Paste is a no-op when there is no attached session (nothing to write to).</summary>
    [Fact]
    public Task Paste_NoSession_IsNoOp()
    {
        return _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl();   // never attached
            control.WritePaste("ignored");
            await DrainAsync();
            // No throw, nothing written — reaching here without exception is the assertion.
            Assert.True(true);
        });
    }

    /// <summary>
    /// Cmd+V round trip: text on the system clipboard is read and written to the PTY. Exercises the real
    /// clipboard read path via the headless TopLevel clipboard.
    /// </summary>
    [Fact]
    public Task PasteFromClipboard_ReadsClipboard_AndWritesToPty()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            var window = new Window { Content = control, Width = 600, Height = 400 };
            window.Show();
            await DrainAsync();

            var clipboard = TopLevel.GetTopLevel(control)?.Clipboard;
            Assert.NotNull(clipboard);
            await clipboard!.SetTextAsync("PASTED_FROM_CLIPBOARD");

            await control.PasteFromClipboardAsync();
            await DrainAsync();

            Assert.Contains("PASTED_FROM_CLIPBOARD", fake.Writes);

            window.Close();
        });
    }

    /// <summary>Cmd+C copy: the terminal's selection is placed on the system clipboard.</summary>
    [Fact]
    public Task CopySelection_PutsSelectedTextOnClipboard()
    {
        return _fx.DispatchAsync(async () =>
        {
            var fake = new FakePtySession();
            var control = new TerminalControl();
            control.Attach(fake);

            var window = new Window { Content = control, Width = 600, Height = 400 };
            window.Show();
            await DrainAsync();

            fake.FireOutput("COPY_ME_123\r\n");
            await DrainAsync();

            var screen = control.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");
            screen.SelectAll();
            await DrainAsync();

            bool copied = await control.CopySelectionToClipboardAsync();
            Assert.True(copied, "SelectAll should yield a non-empty selection to copy");

            var clipboard = TopLevel.GetTopLevel(control)?.Clipboard;
            var text = await clipboard!.TryGetTextAsync();
            Assert.Contains("COPY_ME_123", text);

            window.Close();
        });
    }
}
