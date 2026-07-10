using Styloagent.App.ViewModels;
using Styloagent.Core.Hooks;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Verifies the idle-gated auto-reveal and Alt-jump focus invariants.
///
/// Seam: RevealPane increments AutoActivateCountForTest (focus:false) and
/// JumpFocusCountForTest (focus:true) so tests can assert the right code path was
/// taken without needing to mock the concrete StyloagentDockFactory.
/// </summary>
public class AttentionRevealTests
{
    /// <summary>
    /// When the human is idle (no RecordInput), a permission_prompt hook event should
    /// auto-reveal (activate) the waiting tab, but MUST NOT call SetFocusedDockable.
    ///
    /// We add a second agent so the hook fires on a background pane (not the currently
    /// active tab), which makes AutoReveal.Decide return non-null.
    /// </summary>
    [Fact]
    public async Task Auto_reveal_activates_but_does_not_focus_when_idle()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());

            // Add a second agent so we have a background pane to reveal.
            vm.AddAgentCommand.Execute(null);
            // The second pane's hookId is the second key in the dictionary.
            var secondHookId = vm.SecondHookIdForTest();

            // Switch back to the first pane so the second is NOT active.
            vm.SelectPaneCommand.Execute(vm.Panes[0]);

            // No RecordInput → idle → hook on the non-active second pane → auto-reveal fires.
            vm.DispatchHookForTest(new HookEvent(secondHookId, "Notification", "permission_prompt", null, null, null));

            Assert.True(vm.AutoActivateCountForTest >= 1, "auto-reveal should activate the tab");
            Assert.Equal(0, vm.JumpFocusCountForTest);  // focus invariant: no keyboard grab
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// When the human recently typed (busy), a hook event must NOT auto-reveal,
    /// but the pane must still be queued in the attention queue.
    /// Uses the second pane (not active) so AutoReveal.Decide would otherwise fire.
    /// </summary>
    [Fact]
    public async Task Busy_human_suppresses_auto_reveal()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            // Add a second agent and switch back to first so second is inactive.
            vm.AddAgentCommand.Execute(null);
            var secondHookId = vm.SecondHookIdForTest();
            vm.SelectPaneCommand.Execute(vm.Panes[0]);

            vm.InteractionForTest().RecordInput();          // human just typed → busy

            vm.DispatchHookForTest(new HookEvent(secondHookId, "Notification", "permission_prompt", null, null, null));

            Assert.Equal(0, vm.AutoActivateCountForTest);  // suppressed while busy
            Assert.Equal(1, vm.WaitingCount);              // still queued
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// JumpToNextWaiting is a human-initiated command that MUST call SetFocusedDockable
    /// (the explicit opt-in to keyboard focus).
    /// Uses the second pane so the queue is non-empty and the head is a real pane.
    /// </summary>
    [Fact]
    public async Task JumpToNextWaiting_focuses_the_oldest_waiter()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            // Add a second agent and switch back to first.
            vm.AddAgentCommand.Execute(null);
            var secondHookId = vm.SecondHookIdForTest();
            vm.SelectPaneCommand.Execute(vm.Panes[0]);

            vm.InteractionForTest().RecordInput();          // busy so nothing auto-reveals
            vm.DispatchHookForTest(new HookEvent(secondHookId, "Notification", "permission_prompt", null, null, null));

            vm.JumpToNextWaitingCommand.Execute(null);

            Assert.True(vm.JumpFocusCountForTest >= 1, "explicit jump DOES focus");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Trigger (b): a waiter that arrived while the human was BUSY must be auto-revealed
    /// (without focus) when the human later becomes idle — proven by calling OnIdleTick()
    /// after advancing a controllable clock past the IdleWindow.
    /// </summary>
    [Fact]
    public async Task Idle_tick_reveals_a_waiter_that_was_queued_while_busy()
    {
        // Controllable clock starts at a fixed point in time.
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        MainWindowViewModel.InteractionClockForTest = () => now;

        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());

            // Add a second agent and switch back to first so the second is NOT active.
            vm.AddAgentCommand.Execute(null);
            var secondHookId = vm.SecondHookIdForTest();
            vm.SelectPaneCommand.Execute(vm.Panes[0]);

            // Human types at `now` → busy.
            vm.InteractionForTest().RecordInput();

            // A permission_prompt hook fires on the background pane while the human is busy.
            vm.DispatchHookForTest(new HookEvent(secondHookId, "Notification", "permission_prompt", null, null, null));

            // Suppressed: human is still busy, nothing revealed yet.
            Assert.Equal(0, vm.AutoActivateCountForTest);
            Assert.Equal(1, vm.WaitingCount);

            // Advance clock past the 4 s IdleWindow so the human is now idle.
            now += TimeSpan.FromSeconds(5);

            // Simulate the idle-timer tick.
            vm.OnIdleTick();

            // Trigger (b) fires: the queued waiter is auto-revealed without focus.
            Assert.True(vm.AutoActivateCountForTest >= 1, "idle tick should reveal the queued waiter");
            Assert.Equal(0, vm.JumpFocusCountForTest);   // focus invariant: idle path never focuses
        }
        finally
        {
            MainWindowViewModel.InteractionClockForTest = null;
            Directory.Delete(root, recursive: true);
        }
    }
}
