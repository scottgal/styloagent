using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class TerminalInputTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TerminalInputTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    // Before the fix, digits/symbols were dropped (only A–Z/Space were handled) and the control
    // wasn't focusable — so typing "1" at claude's trust prompt did nothing. This raises the REAL
    // TextInput + KeyDown events and asserts they reach the session's WriteAsync.
    [Fact]
    public async Task Typing_a_digit_and_Enter_reach_the_session()
    {
        var writes = new ConcurrentQueue<string>();

        await _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl();
            var fake = new FakePtySession { OnWrite = s => writes.Enqueue(s) };
            var window = new Window { Content = control, Width = 400, Height = 300 };
            window.Show();

            // Let layout settle before attaching (re-parenting during Show can otherwise detach).
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            control.Attach(fake);
            control.Focus();

            // REAL printable input (a digit) via the TextInput event — this was dropped before.
            control.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = "1",
            });

            // REAL Enter via the KeyDown event.
            control.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
            });

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.Close();
        });

        var all = writes.ToArray();
        Assert.Contains("1", all);
        Assert.Contains("\r", all);
    }

    [Fact]
    public async Task Control_is_focusable()
    {
        await _fx.DispatchAsync(() =>
        {
            var control = new TerminalControl();
            Assert.True(control.Focusable, "TerminalControl must be focusable to receive keyboard input.");
            return Task.CompletedTask;
        });
    }

    // The terminal must PUBLISH the operator's compose window (OperatorInputState) so a message-delivery
    // nudge (PtyMessageInjector) defers instead of typing into the operator's half-finished line.
    [Fact]
    public async Task Operator_compose_state_is_published_for_the_injector()
    {
        await _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl();
            var fake = new FakePtySession();
            var window = new Window { Content = control, Width = 400, Height = 300 };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            control.Attach(fake);
            control.Focus();

            // Fresh session — not composing.
            Assert.False(OperatorInputState.IsComposing(fake));

            // Operator starts a line → composing.
            control.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "h" });
            Assert.True(OperatorInputState.IsComposing(fake), "typing should open the compose window");

            // Operator submits (Enter) → compose window closes.
            control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.False(OperatorInputState.IsComposing(fake), "Enter should close the compose window");

            // Mid-line again, then detach → cleared regardless of compose state.
            control.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "x" });
            Assert.True(OperatorInputState.IsComposing(fake));
            control.Detach();
            Assert.False(OperatorInputState.IsComposing(fake), "detach must clear the compose flag");

            window.Close();
        });
    }
}
