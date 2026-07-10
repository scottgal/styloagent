using Styloagent.App.ViewModels;
using Styloagent.Core.Hooks;

namespace Styloagent.App.Tests;

public class AttentionQueueVmTests
{
    // Drives a hook event through the VM the same way the channel would.
    private static HookEvent Notify(string agentId, string type)
        => new(agentId, "Notification", type, null, null, null);

    [Fact]
    public async Task Waiting_agent_enters_the_queue_and_leaving_removes_it()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var hookId = vm.FirstHookIdForTest();
            Assert.Empty(vm.AttentionQueue);

            vm.DispatchHookForTest(Notify(hookId, "permission_prompt"));   // → WaitingForHuman
            Assert.Equal(1, vm.WaitingCount);
            Assert.Contains("waiting", vm.AttentionHudText);

            vm.DispatchHookForTest(Notify(hookId, "idle_prompt"));         // → Idle (not waiting)
            Assert.Empty(vm.AttentionQueue);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
