using Avalonia.Controls;
using Avalonia.Threading;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class TerminalControlStringTests(HeadlessAvaloniaFixture fixture)
{
    [Theory]
    [InlineData("\u001b_Gi=31337,s=1,v=1,a=q,t=d,f=24;AAAA\u001b\\")]
    [InlineData("\u001bP+q4d73\u001b\\")]
    [InlineData("\u001bXignored\u001b\\")]
    [InlineData("\u001b^ignored\u001b\\")]
    [InlineData("\u009fignored\u009c")]
    public void Unsupported_control_strings_are_removed_at_every_chunk_boundary(string probe)
    {
        var input = "before" + probe + "\u001b[31mafter\u001b[0m\u001b]0;title\u0007";
        const string expected = "before\u001b[31mafter\u001b[0m\u001b]0;title\u0007";
        for (var split = 0; split <= input.Length; split++)
        {
            var filter = new TerminalControlStringFilter();
            Assert.Equal(expected, filter.Filter(input[..split]) + filter.Filter(input[split..]));
        }
        var byCharacter = new TerminalControlStringFilter();
        Assert.Equal(expected, string.Concat(input.Select(ch => byCharacter.Filter(ch.ToString()))));
    }

    [Theory]
    [InlineData('\u0018')]
    [InlineData('\u001a')]
    public void Cancelled_control_string_does_not_swallow_following_text(char cancel)
    {
        Assert.Equal("after", new TerminalControlStringFilter().Filter("\u001b_ignored" + cancel + "after"));
    }

    [Fact]
    public Task OpenTUI_probe_does_not_swallow_the_rendered_screen()
        => fixture.DispatchAsync(async () =>
        {
            var terminal = new TerminalControl();
            var pty = new FakePtySession();
            var window = new Window { Content = terminal, Width = 900, Height = 600 };
            window.Show();
            try
            {
                terminal.Attach(pty);
                // Real startup ordering, including a split ST terminator and alternate-buffer redraw.
                pty.FireOutput("\u001bP+q4d73\u001b\\\u001b_Gi=31337,s=1,v=1,a=q,t=d,f=24;AAAA\u001b");
                pty.FireOutput("\\\u001b[?1049h\u001b[2J\u001b[3;2HOPENCODE_SCREEN_READY");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.Contains("OPENCODE_SCREEN_READY", terminal.RenderedText);
                Assert.DoesNotContain("Gi=31337", terminal.RenderedText);
            }
            finally { window.Close(); }
        });
}
