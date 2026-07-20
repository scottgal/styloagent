using Avalonia.Controls;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using SkiaSharp;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public sealed class ImageDocumentViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public ImageDocumentViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task ImageDocumentView_loads_and_renders_a_dropped_image()
        => _fx.DispatchAsync(async () =>
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/screenshots/welcome.png"));
            var doc = new ImageDocumentViewModel("welcome", path);
            Assert.True(doc.IsValid);

            var view = new ImageDocumentView { DataContext = doc };
            var window = new Window { Width = 800, Height = 600, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);

            Assert.NotNull(window.GetVisualDescendants().OfType<Image>().SingleOrDefault());

            var screenshot = Path.Combine(Path.GetTempPath(), "styloagent-image-view.png");
            await ScreenshotCapture.CaptureControlAsync(window, view, screenshot);
            using var bitmap = SKBitmap.Decode(screenshot);
            Assert.NotNull(bitmap);
            Assert.True(bitmap!.Width > 0 && bitmap.Height > 0);
            window.Close();
        });
}
