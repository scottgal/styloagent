using Avalonia.Controls;
using Avalonia.Threading;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class TerminalFocusTests(HeadlessAvaloniaFixture fixture)
{
    [Fact]
    public async Task Attaching_and_reattaching_a_terminal_preserves_operator_keyboard_focus()
    {
        await fixture.DispatchAsync(async () =>
        {
            var input = new TextBox();
            var panel = new StackPanel { Children = { input } };
            var window = new Window { Content = panel, Width = 800, Height = 600 };
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                input.Focus();
                Assert.True(input.IsFocused);
                var terminal = new TerminalControl();
                panel.Children.Add(terminal);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.True(input.IsFocused);
                Assert.False(terminal.IsFocused);

                panel.Children.Remove(terminal);
                panel.Children.Add(terminal);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.True(input.IsFocused);
                Assert.False(terminal.IsFocused);

                // Explicit operator navigation can still focus the terminal.
                terminal.Focus();
                Assert.True(terminal.IsFocused);
                Assert.False(input.IsFocused);
            }
            finally { window.Close(); }
        });
    }
}
