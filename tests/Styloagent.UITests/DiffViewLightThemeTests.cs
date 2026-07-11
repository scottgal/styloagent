using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Git;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Renders <see cref="DiffView"/> under the Light theme variant and verifies that diff content is
/// visible.  The screenshot at /tmp/styloagent-diff-light.png provides visual confirmation that
/// dark text is readable on the pale diff-row backgrounds.
/// </summary>
[Collection("Avalonia")]
public class DiffViewLightThemeTests(HeadlessAvaloniaFixture fx) : IDisposable
{
    public void Dispose() { }

    [Fact]
    public Task DiffView_light_theme_renders_readable_diff_rows()
    {
        return fx.DispatchAsync(async () =>
        {
            // Save previous variant so we can restore it — avoid polluting other tests.
            var previousVariant = Application.Current?.RequestedThemeVariant;

            try
            {
                // Switch to light theme (mirrors MainWindowViewModel.OnIsLightThemeChanged).
                if (Application.Current is not null)
                    Application.Current.RequestedThemeVariant = ThemeVariant.Light;

                var vm = new DiffViewModel
                {
                    File = new FileDiff("Program.cs", 2, 2, false, new[]
                    {
                        new DiffLine(DiffLineKind.Header,  "@@ -1,3 +1,3 @@", 0, 0),
                        new DiffLine(DiffLineKind.Context, "using System;",    1, 1),
                        new DiffLine(DiffLineKind.Deleted, "Console.Write(\"old\");", 2, 0),
                        new DiffLine(DiffLineKind.Added,   "Console.Write(\"new\");", 0, 2),
                    }),
                };

                var view   = new DiffView { DataContext = vm };
                var window = new Window { Width = 640, Height = 400, Content = view };
                window.Show();

                await HeadlessRender.SettleAsync(window);

                // Assert diff content rendered.
                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text ?? string.Empty)
                    .ToList();

                Assert.Contains(texts, s => s.Contains("new"));
                Assert.Contains(texts, s => s.Contains("old"));

                // Screenshot for visual inspection of readability.
                await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/styloagent-diff-light.png");

                window.Close();
            }
            finally
            {
                // Restore the original theme variant.
                if (Application.Current is not null)
                    Application.Current.RequestedThemeVariant = previousVariant;
            }
        });
    }
}
