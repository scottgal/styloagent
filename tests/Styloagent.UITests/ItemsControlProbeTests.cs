using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class ItemsControlProbeTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public ItemsControlProbeTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    // DOCUMENTED LIMITATION: ItemsControl does NOT materialize its item containers in the
    // headless render (this test fails with 0 of 3). So headless screenshots can't verify
    // ItemsControl-based views (Agents roster, bus feed) — they DO render in the real app.
    // Kept as a skipped record so we don't chase this again.
    [Fact(Skip = "Headless ItemsControl does not materialize item containers; verify these views in the running app.")]
    public async Task ItemsControl_materializes_items()
    {
        int textBlocks = 0;
        await _fx.DispatchAsync(async () =>
        {
            var ic = new ItemsControl
            {
                ItemsSource = new[] { "ALPHA_ROW", "BETA_ROW", "GAMMA_ROW" },
                ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s }, true),
            };
            var window = new Window { Width = 300, Height = 200, Content = ic };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            textBlocks = window.GetVisualDescendants().OfType<TextBlock>()
                .Count(t => t.Text is "ALPHA_ROW" or "BETA_ROW" or "GAMMA_ROW");
            window.Close();
        });

        Assert.Equal(3, textBlocks);
    }
}
