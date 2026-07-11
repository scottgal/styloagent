using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia-Markdown")]
public class DiagramViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public DiagramViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Diagram_document_renders_its_generated_markdown_text()
    {
        return _fx.DispatchAsync(async () =>
        {
            var doc = new DiagramDocumentViewModel("System Map", DiagramKind.SystemMap,
                () => "# System Map\n\nThe fleet map heading renders.");
            var view = new DiagramDocumentView { DataContext = doc };
            var window = new Window { Width = 560, Height = 360, Content = view };
            window.Show();

            int Texts() => window.GetVisualDescendants().OfType<TextBlock>().Count();
            for (int i = 0; i < 40 && Texts() < 1; i++)
            {
                await HeadlessRender.SettleAsync(window);
                await Task.Delay(25);
            }

            Assert.True(Texts() >= 1, "diagram markdown should render into text");

            // Verify Refresh button is present in visual tree
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            Assert.True(buttons.Any(b => b.Content?.ToString()?.Contains("Refresh") == true),
                "a Refresh button should be present in DiagramDocumentView");

            // Verify Live ToggleButton is present in visual tree
            var toggleButtons = window.GetVisualDescendants().OfType<ToggleButton>().ToList();
            Assert.True(toggleButtons.Any(b => b.Content?.ToString()?.Contains("Live") == true),
                "a Live ToggleButton should be present in DiagramDocumentView");

            window.Close();
        });
    }
}
