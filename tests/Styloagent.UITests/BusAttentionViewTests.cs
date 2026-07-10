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

                var vm = new BusViewModel(root, new[] { "alpha-", "beta-", "gamma-" }, new ChannelProjection());
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
}
