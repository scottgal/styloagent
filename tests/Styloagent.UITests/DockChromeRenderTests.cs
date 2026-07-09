using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Regression guard for the root cause of the old "Dock.Avalonia renders nothing" saga:
/// the test harness App was missing <c>DockFluentTheme</c>, so DockControl had no
/// dock-model→control templates and materialized an empty ContentPresenter. That was a harness
/// gap, not a Dock bug. With the theme loaded (as the real App.axaml always did), a DockControl
/// bound to the factory layout renders the full dock CHROME.
///
/// NOTE: Dock hosts document/tool CONTENT (the terminal, bus) via a frame-loop-driven deferred
/// content scheduler that the headless platform can't drive, so the inner AgentPaneView/Terminal
/// is NOT asserted here — that renders in a real GUI. This test asserts the chrome that PROVES
/// the theme + layout + DockControl are wired and rendering.
/// </summary>
[Collection("Avalonia")]
public class DockChromeRenderTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public DockChromeRenderTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task DockControl_with_theme_renders_full_dock_chrome()
    {
        return _fx.DispatchAsync(async () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"styloagent-dockchrome-{Guid.NewGuid():N}");
            var contextDir = Path.Combine(tempRoot, "saved-context");
            Directory.CreateDirectory(contextDir);
            await File.WriteAllTextAsync(Path.Combine(contextDir, "chrome--context.md"), "# chrome");

            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(
                    tempRoot, new FakePtyLauncher(), new FakeFileWatcher());

                var dock = new DockControl
                {
                    Layout = vm.Layout,
                    InitializeLayout = false,
                    InitializeFactory = false,
                };
                var window = new Window { DataContext = vm, Width = 900, Height = 560, Content = dock };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();

                for (int i = 0; i < 6; i++)
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var names = window.GetVisualDescendants().Select(d => d.GetType().Name).ToHashSet();

                // The dock chrome must materialize — this was ALL missing when the theme was absent.
                Assert.Contains("DockControl", names);
                Assert.Contains("RootDockControl", names);
                Assert.Contains("ProportionalDockControl", names);
                Assert.Contains("DocumentControl", names);
                Assert.Contains("DocumentTabStrip", names);
                Assert.Contains("ToolDockControl", names);

                window.Close();
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        });
    }
}
