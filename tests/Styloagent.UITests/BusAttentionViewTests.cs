using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Attention;
using Styloagent.Core.Channel;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class BusAttentionViewTests
{
    private static readonly string[] Prefixes = { "alpha-", "beta-", "gamma-" };
    private readonly HeadlessAvaloniaFixture _fx;
    public BusAttentionViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task BusView_renders_attention_recent_archive_sections()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "busview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            Directory.CreateDirectory(Path.Combine(root, "archive", "inbox"));
            try
            {
                File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");
                File.WriteAllText(Path.Combine(root, "inbox", "beta-done-task.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T11:00:00Z\n\nTask.");
                File.WriteAllText(Path.Combine(root, "outbox", "beta-done-task.reply.md"),
                    "**From:** beta-\n**Timestamp:** 2024-01-10T11:05:00Z\n\nDone.");
                File.WriteAllText(Path.Combine(root, "archive", "inbox", "gamma-old-thing.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-09T09:00:00Z\n\nOld.");

                var vm = new BusViewModel(root, Prefixes, new ChannelProjection());
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 320, Height = 480, Content = view };
                window.Show();

                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, s => s.Contains("NEEDS ATTENTION"));
                Assert.Contains(texts, s => s.Contains("RECENT"));
                Assert.Contains(texts, s => s.Contains("ARCHIVE"));
                Assert.Contains(texts, s => s.Contains("open"));   // alpha subject row materialized

                await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-bus-attention.png");
                window.Close();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    // The 2-state upgrade (signal-bus-viewer-fadecollapse-completed-message): an active (unreplied)
    // thread shows a WAITING pill, and the handled threads auto-collapse into the Archive drawer
    // (Expander collapsed by default) so the active list stays short.
    [Fact]
    public Task BusView_shows_a_waiting_pill_and_auto_collapses_the_archive()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "buspill-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            Directory.CreateDirectory(Path.Combine(root, "archive", "inbox"));
            try
            {
                File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");          // unreplied → Attention/WAITING
                File.WriteAllText(Path.Combine(root, "inbox", "beta-done-task.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T11:00:00Z\n\nTask.");
                File.WriteAllText(Path.Combine(root, "outbox", "beta-done-task.reply.md"),
                    "**From:** beta-\n**Timestamp:** 2024-01-10T11:05:00Z\n\nDone.");       // replied → Archive/DONE

                var vm = new BusViewModel(root, Prefixes, new ChannelProjection());
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 320, Height = 480, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                // The active thread carries a WAITING pill.
                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, s => s == "WAITING");

                // The Archive drawer is collapsed by default (handled threads tucked away).
                var expander = window.GetVisualDescendants().OfType<Expander>().Single();
                Assert.False(expander.IsExpanded);

                window.Close();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    // The real fix (bus-viewer Seen-state): viewing an attention thread DEMOTES it out of NEEDS
    // ATTENTION into Recent, so the list stops screaming; an Archive (✕) affordance is offered while open.
    [Fact]
    public Task BusView_opening_a_thread_demotes_it_out_of_needs_attention()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "busseen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            try
            {
                File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");   // unreplied → Attention/WAITING

                var vm = new BusViewModel(root, Prefixes, new ChannelProjection());
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 320, Height = 480, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                // Starts in NEEDS ATTENTION with a WAITING pill; an Archive (✕) button is offered.
                var before = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(before, s => s == "WAITING");
                Assert.Contains(before, s => s == "✕");
                Assert.Single(vm.AttentionThreads);

                // Operator opens the thread → marks it SEEN → a reload demotes it out of NEEDS ATTENTION.
                vm.OpenThreadCommand.Execute(vm.AttentionThreads[0]);
                await vm.LoadAsync();
                await HeadlessRender.SettleAsync(window);

                Assert.Empty(vm.AttentionThreads);                                   // NEEDS ATTENTION shrank
                Assert.Contains(vm.RecentThreads, t => t.Key.Contains("open-question"));

                window.Close();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    // The middle rung: an unreplied thread whose note a recipient has picked up (PickupProjection) shows a
    // WORKING pill instead of WAITING — the "being worked on" signal.
    [Fact]
    public Task BusView_shows_a_working_pill_when_the_thread_is_picked_up()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "busworking-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            try
            {
                File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");   // unreplied inbound

                // isPickedUp true → the recipient drained it → WORKING (not WAITING).
                var vm = new BusViewModel(root, Prefixes, new ChannelProjection(), isPickedUp: (_, _) => true);
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 320, Height = 480, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, s => s == "WORKING");
                Assert.DoesNotContain(texts, s => s == "WAITING");

                window.Close();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    // BUG 1 (LIVE pickup): the WORKING pill must go live when a recipient DRAINS a delivered note. The
    // pickup signal lives under the temp hooks `deliver/` dir — NOT the channel — so the channel watcher
    // never sees the drain and the pill would freeze at WAITING. The viewer now polls the pickup dir;
    // PollPickupOnce drives that path deterministically (the 750ms timer is the live/restart-verified twin).
    [Fact]
    public Task BusView_pickup_poll_brings_the_working_pill_live_after_a_drain()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "buspickup-" + Guid.NewGuid().ToString("N"));
            var hooksDir = Path.Combine(Path.GetTempPath(), "buspickup-hooks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            Directory.CreateDirectory(hooksDir);
            try
            {
                var msgPath = Path.Combine(root, "inbox", "alpha-open-question.md");
                File.WriteAllText(msgPath, "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");

                // Real pickup store: deliver the note to alpha- and leave it PENDING (a push note still
                // references its path) → PickedUp == false → WAITING.
                var pending = new PendingInbox(hooksDir);
                pending.Enqueue("alpha-", "[bus] normal message — read it: " + msgPath,
                    pushing: true, deliveredPath: msgPath);
                var pickup = new PickupProjection(pending);
                var deliverDir = DeliveryHookCommands.DeliverDir(hooksDir);

                var vm = new BusViewModel(root, Prefixes, new ChannelProjection(),
                    isPickedUp: pickup.IsPickedUp, pickupWatchDir: deliverDir);
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                // Delivered but still pending → WAITING (not yet picked up).
                Assert.False(pending.PickedUp("alpha-", msgPath));
                Assert.Equal("WAITING", ThreadPill(vm, "open-question"));

                // Recipient drains the note (turn-boundary hook / check_inbox) → now PICKED UP.
                pending.DrainFormatted("alpha-");
                Assert.True(pending.PickedUp("alpha-", msgPath));

                // The poll detects the deliver-dir change and schedules a reload — the link the channel
                // watcher can never make. Drive it deterministically, then apply the reprojection.
                Assert.True(vm.PollPickupOnce());
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                Assert.Equal("WORKING", ThreadPill(vm, "open-question"));   // pill went LIVE

                // No further change → the poll stays quiet (no churn).
                Assert.False(vm.PollPickupOnce());

                vm.Dispose();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                if (Directory.Exists(hooksDir)) Directory.Delete(hooksDir, recursive: true);
            }
        });
    }

    private static string ThreadPill(BusViewModel vm, string keyFragment) =>
        vm.AttentionThreads.Concat(vm.RecentThreads).Concat(vm.ArchivedThreads)
          .First(t => t.Key.Contains(keyFragment)).StatusPillText;

    // Archive (✕): dismissing a thread marks it DONE and drops it out of NEEDS ATTENTION into the Archive drawer.
    [Fact]
    public Task BusView_archiving_a_thread_marks_it_done_and_leaves_needs_attention()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "busarchive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            try
            {
                File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");

                var vm = new BusViewModel(root, Prefixes, new ChannelProjection());
                await vm.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 320, Height = 480, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);
                Assert.Single(vm.AttentionThreads);

                vm.ArchiveThreadCommand.Execute(vm.AttentionThreads[0]);   // operator dismisses it
                Assert.Equal("DONE", vm.ArchivedThreads.Concat(vm.AttentionThreads)
                    .First(t => t.Key.Contains("open-question")).StatusPillText);
                await vm.LoadAsync();                                       // reload re-sections it
                await HeadlessRender.SettleAsync(window);

                Assert.Empty(vm.AttentionThreads);
                Assert.Contains(vm.ArchivedThreads, t => t.Key.Contains("open-question"));

                window.Close();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }
}
