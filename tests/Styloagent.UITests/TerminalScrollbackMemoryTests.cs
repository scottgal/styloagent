using Avalonia.Controls;
using Avalonia.Threading;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.Terminal;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Memory guard for the cockpit's largest per-pane allocation.
///
/// P0 (operator-reported, force-quit): ab95e73 raised the terminal scrollback cap 1,000 → 10,000 without
/// accounting for the cost. Retention grows at roughly 11.5 KB per retained line and there is ONE terminal
/// PER AGENT, so a 7-agent fleet went from ~80 MB of scrollback to ~800 MB — and because a terminal only
/// reaches its cap after ~10,000 lines of output (hours), memory climbed for hours and read as an
/// unbounded leak. These tests pin BOTH halves of that: the cap is bounded and configurable, and
/// retention actually plateaus at the cap instead of growing forever.
/// </summary>
[Collection("Avalonia")]
public class TerminalScrollbackMemoryTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public TerminalScrollbackMemoryTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static long Settled()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [Fact]
    public void DefaultScrollback_StaysWithinTheFleetMemoryBudget()
    {
        // At ~11.5 KB/line/terminal, 2,000 lines ≈ 23 MB per pane — about 275 MB for a 12-agent fleet.
        // 10,000 (the regression) would be ~1.4 GB for the same fleet. If someone raises this default,
        // this test should fail and force the fleet-wide arithmetic to be redone.
        // Asserts the COMPILE-TIME default, not the live global — other tests legitimately retune the
        // running value, and this guard is about what ships, not about transient test state.
        Assert.True(TerminalControl.DefaultScrollbackLines <= 2_000,
            $"Default terminal scrollback is {TerminalControl.DefaultScrollbackLines} lines. At ~11.5 KB/line per "
            + "terminal, and one terminal per agent, anything above 2,000 puts a normal fleet into "
            + "multi-hundred-MB territory in scrollback alone. Raise the default only with that budget in mind.");
    }

    [Fact]
    public void SetGlobalScrollback_ClampsToASaneRange()
    {
        var original = TerminalControl.GlobalScrollback;
        try
        {
            TerminalControl.SetGlobalScrollback(5);
            Assert.Equal(200, TerminalControl.GlobalScrollback);        // floor: a usable terminal

            TerminalControl.SetGlobalScrollback(10_000_000);
            Assert.Equal(50_000, TerminalControl.GlobalScrollback);     // ceiling: refuse to OOM the box

            TerminalControl.SetGlobalScrollback(4_000);
            Assert.Equal(4_000, TerminalControl.GlobalScrollback);      // in range: honoured
        }
        finally { TerminalControl.SetGlobalScrollback(original); }
    }

    [Fact]
    public async Task Retention_PlateausAtTheCap_RatherThanGrowingForever()
    {
        var original = TerminalControl.GlobalScrollback;
        long firstSegment = 0, lastSegment = 0;
        try
        {
            TerminalControl.SetGlobalScrollback(1_000);   // small cap keeps the test quick

            await _fx.DispatchAsync(async () =>
            {
                var fake = new FakePtySession();
                var terminal = new TerminalControl();
                terminal.Attach(fake);
                var window = new Window { Content = terminal, Width = 900, Height = 500 };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                async Task PumpAsync(int lines, int from)
                {
                    for (int batch = 0; batch < lines / 100; batch++)
                    {
                        for (int n = 0; n < 100; n++)
                            fake.FireOutput($"line {from + batch * 100 + n}: the quick brown fox jumps over the lazy dog\r\n");
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    }
                }

                // Segment 1 fills well past the cap; segment 3 is deep in steady state.
                var b0 = Settled();
                await PumpAsync(3_000, 0);
                firstSegment = Settled() - b0;

                await PumpAsync(3_000, 3_000);

                var b2 = Settled();
                await PumpAsync(3_000, 6_000);
                lastSegment = Settled() - b2;

                window.Close();
            });
        }
        finally { TerminalControl.SetGlobalScrollback(original); }

        // Past the cap, eviction must offset new output: the steady-state segment retains a small
        // fraction of the fill segment. Growing at the same rate would mean scrollback never releases.
        Assert.True(lastSegment < Math.Max(firstSegment / 4, 2 * 1024 * 1024),
            $"Terminal retention did not plateau: filling to the cap retained {firstSegment:N0} B, but a "
            + $"later steady-state segment of the same size still retained {lastSegment:N0} B. Scrollback "
            + "eviction is not releasing — this is an unbounded terminal leak, not a bounded buffer.");
    }
}
