using Styloagent.App.Services;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.App.Tests;

public class PtyMessageInjectorTests
{
    [Fact]
    public async Task Inject_without_break_types_and_submits()
    {
        var pty = new FakePty();
        var injector = new PtyMessageInjector(id => id == "beta" ? pty : null);

        await injector.InjectAsync("beta", "hello", breakFirst: false);

        Assert.Single(pty.Writes);
        Assert.Equal("hello\r", pty.Writes[0]);
    }

    [Fact]
    public async Task Inject_with_break_sends_escape_first_then_submits()
    {
        var pty = new FakePty();
        var injector = new PtyMessageInjector(_ => pty);

        await injector.InjectAsync("beta", "hello", breakFirst: true);

        Assert.Equal(2, pty.Writes.Count);
        Assert.Equal("\x1b", pty.Writes[0]);     // ESC breaks the turn
        Assert.Equal("hello\r", pty.Writes[1]);
    }

    [Fact]
    public async Task No_live_session_is_a_no_op()
    {
        var injector = new PtyMessageInjector(_ => null);
        // Must not throw when there is nothing to inject into.
        await injector.InjectAsync("ghost", "hello", breakFirst: true);
    }
}
