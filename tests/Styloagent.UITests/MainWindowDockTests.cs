using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Model;

namespace Styloagent.UITests;

/// <summary>
/// Verifies MainWindow actually renders the agent pane + terminal (the shell hosts a
/// working terminal end-to-end), and exposes the agent as a selectable pane.
/// </summary>
[Collection("Avalonia")]
public class MainWindowDockTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public MainWindowDockTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task MainWindow_renders_agent_pane_and_terminal()
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
                // App.axaml provides these DataTemplates in the real app.
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var descendants = window.GetVisualDescendants().ToList();
                Assert.Contains(descendants, d => d.GetType().Name == "AgentPaneView");
                Assert.Contains(descendants, d => d.GetType().Name == "TerminalControl");
                Assert.True(vm.Panes.Count >= 1, "the seeded agent should be an open pane");
                Assert.NotNull(vm.SelectedPane);

                window.Close();
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        });
    }
}
