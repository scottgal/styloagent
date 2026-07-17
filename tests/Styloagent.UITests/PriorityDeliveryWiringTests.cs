using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Hooks;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// End-to-end proof of the priority-delivery WIRING through a real MainWindowViewModel: a message
/// dropped into the channel is routed to the recipient pane and handed to the delivery service the
/// VM builds. Since the VM now wires an MCP-native <see cref="Styloagent.Core.Channel.PendingInbox"/>
/// (design 2026-07-13-mcp-native-delivery-design.md), an urgent message to a hook-connected, mid-turn
/// recipient is enqueued for that recipient's own Stop hook to force-continue into — it is NOT typed
/// into the live PTY mid-turn (the fragile ESC-break path is gone). Exercises ResolvePty +
/// SnapshotLiveAgents + the ChannelDeliveryCoordinator + PendingInbox wiring. The service-level routing
/// matrix (idle→inject, busy→enqueue, low/info→info file) is unit-tested in Core's MessageDeliveryTests.
/// </summary>
[Collection("Avalonia")]
public class PriorityDeliveryWiringTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public PriorityDeliveryWiringTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class NewPtyLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
            => Task.FromResult<IPtySession>(new FakePtySession());
    }

    private sealed class NoWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static string MakeChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), "deliv-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "saved-context"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        File.WriteAllText(Path.Combine(root, "saved-context", "overview-context.md"), "# overview");
        return root;
    }

    [Fact]
    public Task Urgent_message_to_a_busy_connected_pane_is_not_injected_midturn()
    {
        var root = MakeChannel();
        return _fx.DispatchAsync(async () =>
        {
            MainWindowViewModel? vm = null;
            try
            {
                vm = await MainWindowViewModel.InitializeAsync(root, new NewPtyLauncher(), new NoWatcher());

                var pane = vm.Panes[0];

                // Wait for the pane's claude session to come up (CurrentPty set after spawn).
                for (int i = 0; i < 40 && pane.CurrentPty is null; i++)
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.NotNull(pane.CurrentPty);

                var pty = (FakePtySession)pane.CurrentPty!;
                pane.HookState = AgentHookState.Working;   // hook-connected AND mid-turn (Working != Unknown/Idle)
                pty.IsIdle = false;
                pty.ClearWrites();                          // ignore the spawn's own writes

                // A new urgent message addressed to this agent arrives on the channel.
                File.WriteAllText(
                    Path.Combine(root, "inbox", $"{pane.Prefix}broken-build.md"),
                    "**From:** ci-\n**Priority:** urgent\n\nMain is red.");

                int delivered = await vm.PumpDeliveryForTest();

                // New MCP-native contract: a connected, mid-turn recipient does NOT get typed into mid-turn.
                // The message is enqueued for its own Stop hook to force-continue into, so the PTY sees no
                // ESC-break and no injected content. (The pending-store roundtrip is covered by Core's
                // MessageDeliveryTests; here we prove the VM wires PendingInbox so the fragile inject path
                // is bypassed end-to-end.)
                Assert.Equal(1, delivered);                                             // routed to one recipient
                Assert.DoesNotContain("\x1b", pty.Writes);                              // no mid-turn ESC-break
                Assert.DoesNotContain(pty.Writes, w => w.Contains("broken-build"));     // nothing typed into the pane
            }
            finally
            {
                vm?.Dispose();
            }
        }).ContinueWith(t =>
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            t.GetAwaiter().GetResult();   // surface any assertion failure
        });
    }
}
