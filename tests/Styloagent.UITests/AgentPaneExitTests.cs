using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.Core.Hooks;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.UITests;

/// <summary>
/// Fix 2 — a killed agent must go to Exited.
///
/// A hard kill (or crash) ends the PTY process WITHOUT firing Claude Code's SessionEnd hook, so the
/// hook-driven state machine never advances to Exited and the roster tab stays stuck on whatever it
/// last was — most painfully ⚠ "needs you". The pane must force HookState = Exited straight off the
/// PTY's Exited signal, independent of the hook stream.
/// </summary>
[Collection("Avalonia")]
public class AgentPaneExitTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AgentPaneExitTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static AgentManifestEntry MakeEntry() => new(
        Prefix: "foss-",
        Repo: "/repo",
        Worktree: "/repo/wt-foss",
        LaunchPromptPath: "",
        RestartPromptPath: "",
        SavedContextPath: "/ctx.md",
        Transport: AgentTransport.Local);

    [Fact]
    public Task PtyExit_ForcesHookStateExited_ClearingAStuckNeedsYou()
    {
        return _fx.DispatchAsync(async () =>
        {
            var entry = MakeEntry();
            var launcher = new FakePtyLauncher();
            var session = new AgentSession(entry, launcher, new FakeFileWatcher());
            var vm = new AgentPaneViewModel(session, entry, "foss", "#E57373");

            await vm.SpawnAsync();   // wires the PTY; the pane subscribes to its Exited signal

            // The agent blocks on a human (⚠ needs-you) and is then hard-killed — no SessionEnd hook.
            vm.ApplyHookEvent(new HookEvent("foss", "Notification", "permission_prompt", "Allow?", null, null));
            Assert.Equal(AgentHookState.WaitingForHuman, vm.HookState);
            Assert.True(vm.NeedsYou);

            launcher.Spawned[0].FireExited();   // PTY process dies

            // Marshalled to the UI thread — drain it.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal(AgentHookState.Exited, vm.HookState);
            Assert.False(vm.NeedsYou);               // the stuck ⚠ is cleared
            Assert.Equal("exited", vm.HookStateText);
            Assert.Equal("✕", vm.HookStateGlyph);
        });
    }

    [Fact]
    public Task PtyExit_ForcesHookStateExited_FromWorking()
    {
        return _fx.DispatchAsync(async () =>
        {
            var entry = MakeEntry();
            var launcher = new FakePtyLauncher();
            var session = new AgentSession(entry, launcher, new FakeFileWatcher());
            var vm = new AgentPaneViewModel(session, entry, "foss", "#E57373");

            await vm.SpawnAsync();
            vm.ApplyHookEvent(new HookEvent("foss", "PreToolUse", null, null, null, null));
            Assert.Equal(AgentHookState.Working, vm.HookState);

            launcher.Spawned[0].FireExited();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal(AgentHookState.Exited, vm.HookState);
        });
    }
}
