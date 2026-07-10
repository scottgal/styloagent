using Styloagent.App.Services;
using Xunit;

namespace Styloagent.App.Tests;

public class InteractionMonitorTests
{
    [Fact]
    public void IsBusy_is_true_within_the_window_after_input_and_false_after()
    {
        var now = DateTimeOffset.UtcNow;
        var mon = new InteractionMonitor(() => now);
        mon.RecordInput();

        Assert.True(mon.IsBusy(TimeSpan.FromSeconds(4)));   // just typed

        now = now.AddSeconds(5);                             // 5s later, window 4s
        Assert.False(mon.IsBusy(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void IsBusy_is_false_before_any_input()
        => Assert.False(new InteractionMonitor(() => DateTimeOffset.UtcNow).IsBusy(TimeSpan.FromSeconds(4)));
}
