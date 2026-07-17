using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.Terminal;

namespace Styloagent.UITests;

/// <summary>
/// Lifetime / leak regressions for <see cref="TerminalControl"/>. The P0: the control tracked the app-wide
/// font size via a STATIC event with a strong instance handler, unsubscribed only in
/// <c>OnDetachedFromVisualTree</c> — which never fires for a control orphaned by a layout rebuild. A static
/// event holding strong handler refs is the classic .NET static-event leak: every orphaned terminal (plus
/// its ~1000-row scrollback and Skia/composition resources) was pinned app-lifetime. The fix tracks
/// subscribers WEAKLY so an orphaned control is collectable even if no detach ever fires.
/// </summary>
[Collection("Avalonia")]
public class TerminalControlLifetimeTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public TerminalControlLifetimeTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    // Construct a terminal so that NO strong reference escapes this frame — only a static subscription could
    // keep it alive. NoInlining so the JIT can't extend the temporary's lifetime into the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MakeOrphanedTerminal() => new(new TerminalControl());

    [Fact]
    public async Task OrphanedTerminals_AreCollectable_NotPinnedByGlobalFontTracking()
    {
        var refs = new List<WeakReference>();

        await _fx.DispatchAsync(async () =>
        {
            // These terminals are never added to a window and never detached — the ONLY thing that could
            // keep them alive is the app-wide font-size tracking. That must hold them weakly.
            for (int i = 0; i < 10; i++)
                refs.Add(MakeOrphanedTerminal());

            // Drain the ctor's posted RebuildRows/ApplyFontMetrics so the dispatcher queue isn't a strong root.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        });

        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        int alive = refs.Count(r => r.IsAlive);
        Assert.Equal(0, alive);
    }

    [Fact]
    public async Task SetGlobalFontSize_StillPropagatesToLiveTerminals()
    {
        // Guards the refactor: dropping the static event must NOT drop the "every live terminal tracks the
        // app-wide font size" behaviour. Set a distinct global size and assert a live terminal's render font
        // follows it; restore the default so the shared static state doesn't leak into other tests.
        Exception? lambdaEx = null;
        await _fx.DispatchAsync(async () =>
        {
            try
            {
                var control = new TerminalControl();
                var window = new Window { Content = control, Width = 800, Height = 400, Name = "FontPropagateWindow" };
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                TerminalControl.SetGlobalFontSize(21.0);   // zoom is 1.0 → effective font size 21
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var screen = control.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "ScreenText");
                Assert.Equal(21.0, screen.FontSize, precision: 1);

                window.Close();
            }
            catch (Exception ex) { lambdaEx = ex; }
            finally { TerminalControl.SetGlobalFontSize(13.0); }   // restore default for other tests
        });

        Assert.Null(lambdaEx);
    }
}
