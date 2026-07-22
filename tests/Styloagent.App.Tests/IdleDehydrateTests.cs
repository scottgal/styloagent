using Styloagent.App.ViewModels;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Hooks;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.App.Tests;

public sealed class IdleDehydrateTests : IDisposable
{
    private readonly string _channelRoot;

    public IdleDehydrateTests()
    {
        _channelRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var savedContext = Path.Combine(_channelRoot, "saved-context");
        Directory.CreateDirectory(savedContext);
        File.WriteAllText(Path.Combine(savedContext, "worker-context.md"), "# worker");
    }

    public void Dispose()
    {
        if (Directory.Exists(_channelRoot))
            Directory.Delete(_channelRoot, recursive: true);
    }

    [Fact]
    public async Task Acknowledged_checkpoint_parks_agent_after_idle_threshold()
    {
        var launcher = new FakeLauncher();
        using var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot, launcher, new FakeWatcher { WillChange = true });
        await WaitUntil(() => vm.Pane?.State == SessionState.Live);
        var pane = Assert.IsType<AgentPaneViewModel>(vm.Pane);
        MakeIdle(pane);
        var idleAt = Assert.IsType<DateTimeOffset>(pane.LastActivityAt);

        await vm.CheckAutoDehydrateAsync(idleAt.AddMinutes(29));
        Assert.Equal(SessionState.Live, pane.State);

        await vm.CheckAutoDehydrateAsync(idleAt.AddMinutes(31));

        Assert.Equal(SessionState.Dehydrated, pane.State);
        Assert.True(launcher.Spawned.Single().Disposed);
        Assert.Contains(launcher.Spawned.Single().Writes,
            write => write.Contains("checkpoint your context", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_ack_keeps_terminal_live_and_backs_off_retries()
    {
        var launcher = new FakeLauncher();
        using var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot, launcher, new FakeWatcher { WillChange = false });
        await WaitUntil(() => vm.Pane?.State == SessionState.Live);
        var pane = Assert.IsType<AgentPaneViewModel>(vm.Pane);
        MakeIdle(pane);
        var idleAt = Assert.IsType<DateTimeOffset>(pane.LastActivityAt);

        await vm.CheckAutoDehydrateAsync(idleAt.AddMinutes(31));
        await vm.CheckAutoDehydrateAsync(idleAt.AddMinutes(40));

        Assert.Equal(SessionState.Live, pane.State);
        Assert.False(launcher.Spawned.Single().Disposed);
        Assert.Equal(1, vm.AutoDehydrateAttemptCountForTest);

        await vm.CheckAutoDehydrateAsync(idleAt.AddMinutes(47));
        Assert.Equal(2, vm.AutoDehydrateAttemptCountForTest);
    }

    [Fact]
    public async Task Working_agent_is_never_automatically_parked()
    {
        using var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot, new FakeLauncher(), new FakeWatcher());
        await WaitUntil(() => vm.Pane?.State == SessionState.Live);
        var pane = Assert.IsType<AgentPaneViewModel>(vm.Pane);
        pane.ApplyHookEvent(new HookEvent(pane.Prefix, "PreToolUse", null, null, null, null));
        var activeAt = Assert.IsType<DateTimeOffset>(pane.LastActivityAt);

        await vm.CheckAutoDehydrateAsync(activeAt.AddDays(1));

        Assert.Equal(SessionState.Live, pane.State);
        Assert.Equal(0, vm.AutoDehydrateAttemptCountForTest);
    }

    [Fact]
    public async Task Resuming_work_cancels_pending_idle_checkpoint()
    {
        var watcher = new BlockingWatcher();
        using var vm = await MainWindowViewModel.InitializeAsync(
            _channelRoot, new FakeLauncher(), watcher);
        await WaitUntil(() => vm.Pane?.State == SessionState.Live);
        var pane = Assert.IsType<AgentPaneViewModel>(vm.Pane);
        MakeIdle(pane);
        var idleAt = Assert.IsType<DateTimeOffset>(pane.LastActivityAt);

        var attempt = vm.CheckAutoDehydrateAsync(idleAt.AddMinutes(31));
        await watcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pane.ApplyHookEvent(new HookEvent(pane.Prefix, "UserPromptSubmit", null, null, null, null));
        await attempt.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Live, pane.State);
        Assert.Equal(AgentHookState.Working, pane.HookState);
    }

    private static void MakeIdle(AgentPaneViewModel pane) =>
        pane.ApplyHookEvent(new HookEvent(pane.Prefix, "Notification", "idle_prompt", null, null, null));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class BlockingWatcher : IFileWatcher
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> WaitForChangeAsync(
            string path,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return false;
        }
    }
}
