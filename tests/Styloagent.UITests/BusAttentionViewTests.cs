using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
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

    // The 3-state upgrade (bus-viewer Seen-state): opening an attention thread flips its pill from
    // WAITING to the middle SEEN rung, and an explicit Archive (✕) affordance is offered while open.
    [Fact]
    public Task BusView_opening_a_thread_shows_the_SEEN_pill_and_offers_archive()
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

                // Starts WAITING; an Archive (✕) button is present while the thread is open.
                var before = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(before, s => s == "WAITING");
                Assert.Contains(before, s => s == "✕");
                Assert.Equal("WAITING", vm.AttentionThreads[0].StatusPillText);

                // Operator opens the thread (carousel gesture) → marks it SEEN; the thread pill flips
                // in place (WAITING → SEEN) without a reload.
                vm.OpenThreadCommand.Execute(vm.AttentionThreads[0]);
                await HeadlessRender.SettleAsync(window);

                Assert.Equal("SEEN", vm.AttentionThreads[0].StatusPillText);
                var after = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(after, s => s == "SEEN");   // the middle rung now renders

                window.Close();
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }
}
