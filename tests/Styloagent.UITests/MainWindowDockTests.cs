using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.UITests;

/// <summary>
/// Verifies that MainWindow's content is a DockControl backed by a non-null RootDock,
/// proving the application window truly hosts the Dock layout end-to-end.
/// </summary>
[Collection("Avalonia")]
public class MainWindowDockTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public MainWindowDockTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static AgentManifestEntry MakeEntry(string contextPath) => new(
        Prefix: "test-",
        Repo: "/repo",
        Worktree: "/repo/wt-test",
        LaunchPromptPath: "",
        RestartPromptPath: "",
        SavedContextPath: contextPath,
        Transport: AgentTransport.Local);

    /// <summary>
    /// MainWindow with a seeded MainWindowViewModel hosts a DockControl
    /// whose Layout is a non-null RootDock.
    /// </summary>
    [Fact]
    public Task MainWindow_Content_Is_DockControl_With_RootDock_Layout()
    {
        return _fx.DispatchAsync(async () =>
        {
            // Arrange: create a temp channel dir with one saved-context file so
            // InitializeAsync produces a non-empty layout.
            var tempRoot = Path.Combine(Path.GetTempPath(), $"styloagent-test-{Guid.NewGuid():N}");
            var contextDir = Path.Combine(tempRoot, "saved-context");
            Directory.CreateDirectory(contextDir);
            await File.WriteAllTextAsync(
                Path.Combine(contextDir, "test--context.md"),
                "# test agent context");

            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(
                    tempRoot,
                    new FakePtyLauncher(),
                    new FakeFileWatcher());

                var window = new MainWindow { DataContext = vm };
                window.Show();

                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                // Assert: the window's content is a DockControl
                var dockControl = window.FindControl<DockControl>("DockControl");
                Assert.NotNull(dockControl);

                // Assert: the Layout is a non-null RootDock
                Assert.NotNull(vm.Layout);
                Assert.IsAssignableFrom<IRootDock>(vm.Layout);

                window.Close();
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        });
    }
}
