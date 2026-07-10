using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class ItemsControlProbeTests
{
    private static readonly string[] Rows = { "ALPHA_ROW", "BETA_ROW", "GAMMA_ROW" };
    private readonly HeadlessAvaloniaFixture _fx;
    public ItemsControlProbeTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    // RESOLVED: a headless ItemsControl DOES materialize its item containers — the earlier
    // "0 of 3" was because the test App loaded no FluentTheme, so ItemsControl/ItemsPresenter had
    // no control template and generated no containers (the SAME missing-theme root cause as the old
    // "Dock renders nothing"). With FluentTheme loaded in TestApp, all items materialize. This test
    // now guards that the roster/bus/tab ItemsControls really render headlessly.
    [Fact]
    public async Task ItemsControl_materializes_items()
    {
        int textBlocks = 0;
        await _fx.DispatchAsync(async () =>
        {
            var ic = new ItemsControl
            {
                ItemsSource = Rows,
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
