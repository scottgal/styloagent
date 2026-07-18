using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Top-bar "Shut down" (operator): checkpoint every ACTIVE agent (dehydrate = checkpoint + graceful PTY
/// dispose), flag any that can't, then request the app's graceful close — never hang on a stuck agent.
/// </summary>
public class ShutdownTests
{
    /// <summary>A spawned (Live) pane with a controllable saved-context path + dehydrate-ack behaviour
    /// (FakeWatcher.WillChange = does the checkpoint get acked).</summary>
    private static async Task<AgentPaneViewModel> LivePane(string prefix, string savedCtx, bool willChange)
    {
        var entry = new AgentManifestEntry(prefix, "/repo", "/repo/wt", "", "", savedCtx, AgentTransport.Local);
        var session = new AgentSession(entry, new FakeLauncher(), new FakeWatcher { WillChange = willChange });
        var pane = new AgentPaneViewModel(session, entry, prefix.TrimEnd('-'), "#888888");
        await pane.SpawnAsync();
        return pane;
    }

    private static Task<MainWindowViewModel> NewVm()
        => MainWindowViewModel.InitializeAsync(
            MainWindowViewModelTests.MakeTwoAgentChannel(), new FakeLauncher(), new FakeWatcher());

    [Fact]
    public async Task Shutdown_checkpoints_each_active_agent_then_requests_close()
    {
        var vm = await NewVm();
        var a = await LivePane("aaa-", "/tmp/aaa.md", willChange: true);
        var b = await LivePane("bbb-", "/tmp/bbb.md", willChange: true);
        vm.Panes.Add(a);
        vm.Panes.Add(b);

        bool closed = false;
        vm.RequestShutdown = () => closed = true;
        vm.ConfirmShutdownAsync = _ => Task.FromResult(true);

        await vm.ShutdownCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Dehydrated, a.State);   // checkpointed + graceful dispose
        Assert.Equal(SessionState.Dehydrated, b.State);
        Assert.True(closed);                               // app close requested AFTER checkpointing
    }

    [Fact]
    public async Task Shutdown_cancelled_at_confirm_touches_nothing()
    {
        var vm = await NewVm();
        var a = await LivePane("aaa-", "/tmp/aaa.md", willChange: true);
        vm.Panes.Add(a);

        bool closed = false;
        vm.RequestShutdown = () => closed = true;
        vm.ConfirmShutdownAsync = _ => Task.FromResult(false);   // operator says no

        await vm.ShutdownCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Live, a.State);   // NOT checkpointed
        Assert.False(closed);                        // NOT closed
    }

    [Fact]
    public async Task Shutdown_flags_an_agent_that_fails_to_checkpoint_but_still_closes()
    {
        var vm = await NewVm();
        var bad = await LivePane("bad-", "/tmp/bad.md", willChange: false);   // watcher never acks → dehydrate fails
        vm.Panes.Add(bad);

        bool closed = false;
        vm.RequestShutdown = () => closed = true;
        vm.ConfirmShutdownAsync = _ => Task.FromResult(true);

        await vm.ShutdownCommand.ExecuteAsync(null);

        Assert.NotEqual(SessionState.Dehydrated, bad.State);   // stayed Live (failed to ack)
        Assert.True(closed);                                    // a failing agent does NOT hang shutdown
        Assert.Contains(vm.Timeline.Entries,
            e => e.Description.Contains("bad-") && e.Description.Contains("did not complete"));
    }

    [Fact]
    public async Task Shutdown_falls_back_and_flags_an_agent_with_no_saved_context_path()
    {
        var vm = await NewVm();
        var noctx = await LivePane("noctx-", savedCtx: "", willChange: true);   // CanDehydrate false
        vm.Panes.Add(noctx);

        bool closed = false;
        vm.RequestShutdown = () => closed = true;
        vm.ConfirmShutdownAsync = _ => Task.FromResult(true);

        await vm.ShutdownCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Contains(vm.Timeline.Entries,
            e => e.Description.Contains("noctx-") && e.Description.Contains("no saved-context path"));
    }
}
