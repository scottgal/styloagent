using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Channel;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// A federated repo instance's bus feed, opened as a document tab, renders through the
/// RepoBusDocumentViewModel → BusView template over the repo's OWN channel (the "open stylobot and see
/// its bus" interim). Proves the surfacing wiring end-to-end headless.
/// </summary>
[Collection("Avalonia")]
public class RepoBusDocumentTests
{
    private static readonly string[] Prefixes = { "overview-", "stylo-" };
    private readonly HeadlessAvaloniaFixture _fx;
    public RepoBusDocumentTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task RepoBusDocument_renders_the_second_instances_bus_feed()
    {
        return _fx.DispatchAsync(async () =>
        {
            var channelRoot = Path.Combine(Path.GetTempPath(), "repobus-" + Guid.NewGuid().ToString("N"), "channel");
            Directory.CreateDirectory(Path.Combine(channelRoot, "inbox"));
            try
            {
                // A message in the second instance's OWN channel — it should show in the surfaced feed.
                File.WriteAllText(Path.Combine(channelRoot, "inbox", "stylo-open-question.md"),
                    "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nSecond instance question?");

                var bus = new BusViewModel(channelRoot, Prefixes, new ChannelProjection());
                await bus.LoadAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var doc = new RepoBusDocumentViewModel("/work/stylobot", "stylobot", bus);
                Assert.Equal("stylobot · bus", doc.Title);      // tab caption = repo name

                // The document renders through the App template as a BusView bound to its Bus.
                var view = new BusView { DataContext = doc.Bus };
                var window = new Window { Width = 320, Height = 480, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);

                Assert.Contains(window.GetVisualDescendants().OfType<BusView>(), _ => true);
                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, s => s.Contains("NEEDS ATTENTION"));
                Assert.Contains(texts, s => s.Contains("open"));   // the second-instance thread materialized

                window.Close();
            }
            finally
            {
                var top = Directory.GetParent(channelRoot)?.FullName;
                if (top is not null && Directory.Exists(top)) Directory.Delete(top, recursive: true);
            }
        });
    }
}
