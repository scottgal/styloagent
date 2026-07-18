using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Operator: the current PROJECT NAME shows in the cockpit title. Asserts the OS window title (title bar /
/// dock / window switcher) and the in-app top-chrome label both reflect the project name.
/// </summary>
[Collection("Avalonia")]
public class WindowTitleViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public WindowTitleViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Window_title_and_top_chrome_show_the_project_name()
    {
        return _fx.DispatchAsync(async () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "sty-title-" + Guid.NewGuid().ToString("N"));
            var contextDir = Path.Combine(tempRoot, "saved-context");
            Directory.CreateDirectory(contextDir);
            await File.WriteAllTextAsync(Path.Combine(contextDir, "test--context.md"), "# test agent context");
            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(
                    tempRoot, new FakePtyLauncher(), new FakeFileWatcher());
                vm.SetReposFromOverviews(WorkspaceConfig
                    .For("/ws", "mono", new[] { Path.Combine("/ws", "Styloagent") }).RepoOverviews());

                var window = new MainWindow { DataContext = vm, Width = 900, Height = 500 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                // OS title bar / dock / window switcher.
                Assert.Equal("Styloagent — Styloagent Cockpit", window.Title);

                // In-app top-chrome label.
                var texts = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
                    .Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains(texts, s => s == "Styloagent");

                window.Close();
            }
            finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
        });
    }
}
