using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Model;

namespace Styloagent.UITests;

/// <summary>
/// Verifies MainWindow composes its shell: the centre DockControl renders its dock chrome and holds
/// the seeded agent as the active document, alongside the roster + bus side panels.
///
/// NOTE: the terminal is hosted inside Dock's frame-loop-driven deferred content presenter, which the
/// headless platform can't drive — so AgentPaneView/TerminalControl are NOT asserted here. The
/// terminal's own rendering + input are covered standalone (TerminalScreenshotTests, TerminalInputTests,
/// ShellLayoutTests.AgentPaneView_Attaches_Terminal_When_PtyStarted).
/// </summary>
[Collection("Avalonia")]
public class MainWindowDockTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public MainWindowDockTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task MainWindow_renders_dock_chrome_with_agent_document()
    {
        return _fx.DispatchAsync(async () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"styloagent-test-{Guid.NewGuid():N}");
            var contextDir = Path.Combine(tempRoot, "saved-context");
            Directory.CreateDirectory(contextDir);
            await File.WriteAllTextAsync(Path.Combine(contextDir, "test--context.md"), "# test agent context");

            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(
                    tempRoot, new FakePtyLauncher(), new FakeFileWatcher());

                var window = new MainWindow { DataContext = vm, Width = 800, Height = 500 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var names = window.GetVisualDescendants().Select(d => d.GetType().Name).ToHashSet();
                // The centre DockControl renders its chrome (this is what "renders nothing" used to fail).
                Assert.Contains("DockControl", names);
                Assert.Contains("DocumentControl", names);
                // The roster + bus side panels are still present alongside the dock.
                Assert.Contains("AgentsView", names);
                Assert.Contains("BusView", names);

                // The seeded agent is an open, selected pane, hosted as the active dock document.
                Assert.True(vm.Panes.Count >= 1, "the seeded agent should be an open pane");
                Assert.NotNull(vm.SelectedPane);
                var activeDoc = Assert.IsType<Document>(vm.DocumentDock!.ActiveDockable);
                Assert.Same(vm.SelectedPane, activeDoc.Context);

                window.Close();
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        });
    }
}
