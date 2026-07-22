using Dock.Model.Core;
using Styloagent.App.Dock;
using Styloagent.App.ViewModels;
using Styloagent.Core.Hooks;

namespace Styloagent.App.Tests;

public class ActiveAgentLayoutTests
{
    [Fact]
    public async Task CompactRoster_DoesNotBroadcastHiddenLastOutputUpdatesEverySecond()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        MainWindowViewModel? vm = null;
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var notifications = 0;
            vm.Pane!.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AgentPaneViewModel.LastOutputText)) notifications++;
            };

            Assert.False(vm.ShowRosterLastOutput);
            vm.OnIdleTick();
            Assert.Equal(0, notifications);

            vm.ShowRosterLastOutput = true;
            Assert.Equal(1, notifications); // immediate refresh when the hidden field becomes visible
            vm.OnIdleTick();
            Assert.Equal(2, notifications);
        }
        finally
        {
            vm?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Policy_KeepsActiveAndRecentlyIdleAgents_ButDropsExpiredAndExited()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var retention = TimeSpan.FromSeconds(30);

        Assert.True(ActiveAgentLayoutPolicy.ShouldShow(AgentHookState.Unknown, null, now, retention));
        Assert.True(ActiveAgentLayoutPolicy.ShouldShow(AgentHookState.Working, now.AddHours(-1), now, retention));
        Assert.True(ActiveAgentLayoutPolicy.ShouldShow(AgentHookState.WaitingForHuman, now.AddHours(-1), now, retention));
        Assert.True(ActiveAgentLayoutPolicy.ShouldShow(AgentHookState.Idle, now.AddSeconds(-30), now, retention));
        Assert.False(ActiveAgentLayoutPolicy.ShouldShow(AgentHookState.Idle, now.AddSeconds(-31), now, retention));
        Assert.False(ActiveAgentLayoutPolicy.ShouldShow(AgentHookState.Idle, null, now, retention));
        Assert.False(ActiveAgentLayoutPolicy.ShouldShow(AgentHookState.Exited, now, now, retention));
    }

    [Fact]
    public async Task ActiveLayout_DynamicallyRemovesExpiredIdleAgent_AndRestoresItWhenWorking()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        MainWindowViewModel? vm = null;
        try
        {
            vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.AddAgentCommand.Execute(null);
            Assert.Equal(2, vm.Panes.Count);
            var agent = vm.Panes[1];

            vm.SetLayoutModeCommand.Execute("ActiveAgents");
            Assert.True(vm.IsActiveAgentsLayout);
            Assert.Contains(agent, Deep(vm.Layout!));

            agent.ApplyHookEvent(new HookEvent(agent.Prefix, "Notification", "idle_prompt", null, null, null));
            var idledAt = Assert.IsType<DateTimeOffset>(agent.LastActivityAt);
            vm.RefreshActiveAgentLayout(idledAt.AddSeconds(29));
            Assert.Contains(agent, Deep(vm.Layout!));

            var evaluations = vm.ActiveLayoutMembershipEvaluationCountForTest;
            for (var i = 0; i < 100; i++)
                vm.RefreshActiveAgentLayoutOnTick(idledAt.AddSeconds(29));
            Assert.Equal(evaluations, vm.ActiveLayoutMembershipEvaluationCountForTest);

            vm.RefreshActiveAgentLayoutOnTick(idledAt.AddSeconds(31));
            Assert.Equal(evaluations + 1, vm.ActiveLayoutMembershipEvaluationCountForTest);
            Assert.DoesNotContain(agent, Deep(vm.Layout!));

            agent.ApplyHookEvent(new HookEvent(agent.Prefix, "UserPromptSubmit", null, null, null, null));
            vm.RefreshActiveAgentLayout();
            Assert.Contains(agent, Deep(vm.Layout!));
        }
        finally
        {
            vm?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<IDockable> Deep(IDockable node)
    {
        yield return node;
        if (node is not IDock { VisibleDockables: { } children }) yield break;
        foreach (var child in children)
        foreach (var descendant in Deep(child))
            yield return descendant;
    }
}
