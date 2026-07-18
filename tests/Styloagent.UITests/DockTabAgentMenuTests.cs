using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Operator fix: the per-agent name + ⋯ actions menu + zoom moved ONTO the dock tab (killing the
/// redundant AgentPaneChromeView header row). Asserts the agent tab header materializes the ⋯ actions
/// menu button — scoped to the DocumentTabStrip so it's the TAB's menu, not the roster's ⋯.
/// </summary>
[Collection("Avalonia")]
public class DockTabAgentMenuTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public DockTabAgentMenuTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Agent_tab_header_materializes_the_actions_menu_on_the_tab()
    {
        return _fx.DispatchAsync(async () =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "sty-tabmenu-" + Guid.NewGuid().ToString("N"));
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
                await ScreenshotCapture.CaptureWindowAsync(window, "/tmp/sty-tabmenu.png", settle: true);

                var tabStrip = window.GetVisualDescendants()
                    .FirstOrDefault(d => d.GetType().Name == "DocumentTabStrip");
                Assert.NotNull(tabStrip);

                // The ⋯ actions menu button lives ON the tab (from StyloagentTabHeaderTemplate).
                var dotMenu = ((Visual)tabStrip!).GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => b.Content as string == "⋯");
                Assert.NotNull(dotMenu);

                window.Close();
            }
            finally { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
        });
    }
}
