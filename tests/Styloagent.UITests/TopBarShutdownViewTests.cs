using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Operator: a top-bar "Shut down" that checkpoints every active agent then closes gracefully. Asserts the
/// button renders and is wired to ShutdownCommand. (The confirm dialog + live checkpoint-all→close is
/// restart/manual-verified; the command behaviour is covered headlessly in App.Tests.ShutdownTests.)
/// </summary>
[Collection("Avalonia")]
public class TopBarShutdownViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TopBarShutdownViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Top_bar_has_a_shutdown_button_bound_to_the_command()
    {
        return _fx.DispatchAsync(async () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "sty-shutdown-" + Guid.NewGuid().ToString("N"));
            var contextDir = Path.Combine(tempRoot, "saved-context");
            Directory.CreateDirectory(contextDir);
            await File.WriteAllTextAsync(Path.Combine(contextDir, "test--context.md"), "# test agent context");
            try
            {
                var vm = await MainWindowViewModel.InitializeAsync(
                    tempRoot, new FakePtyLauncher(), new FakeFileWatcher());

                var window = new MainWindow { DataContext = vm, Width = 900, Height = 500 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, s => s == "Shut down");

                var button = window.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Name == "ShutdownButton");
                Assert.NotNull(button);
                Assert.NotNull(button!.Command);   // wired to ShutdownCommand

                window.Close();
            }
            finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
        });
    }
}
