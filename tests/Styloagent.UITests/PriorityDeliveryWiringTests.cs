using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Hooks;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// End-to-end proof of the priority-delivery WIRING through a real MainWindowViewModel: a message
/// dropped into the channel is routed to the recipient pane and injected into its live session,
/// with ESC-break applied when the recipient is mid-turn. Exercises ResolvePty + SnapshotLiveAgents
/// + the ChannelDeliveryCoordinator the VM builds.
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
    public Task Urgent_message_is_injected_into_the_recipient_pane_with_break()
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
                pane.HookState = AgentHookState.Working;   // mid-turn → urgent should ESC-break
                pty.ClearWrites();                          // ignore the spawn's own writes

                // A new urgent message addressed to this agent arrives on the channel.
                File.WriteAllText(
                    Path.Combine(root, "inbox", $"{pane.Prefix}broken-build.md"),
                    "**From:** ci-\n**Priority:** urgent\n\nMain is red.");

                int delivered = await vm.PumpDeliveryForTest();

                Assert.Equal(1, delivered);
                Assert.Contains("\x1b", pty.Writes);                                   // ESC broke the turn
                Assert.Contains(pty.Writes, w => w.Contains("broken-build") && w.Contains("urgent"));
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
