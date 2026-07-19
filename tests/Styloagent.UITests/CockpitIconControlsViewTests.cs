using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Avalonia.Fluent;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Xunit;

namespace Styloagent.UITests;

/// <summary>Protects the compact top-bar utility controls' tooltip and accessibility contract.</summary>
[Collection("Avalonia")]
public class CockpitIconControlsViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public CockpitIconControlsViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    [Fact]
    public Task Top_bar_utility_actions_are_icon_first_and_accessible()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "sty-icon-controls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainWindowViewModel? vm = null;
            try
            {
                vm = await MainWindowViewModel.InitializeAsync(root, new FakePtyLauncher(), new FakeFileWatcher());
                var window = new MainWindow { DataContext = vm, Width = 1000, Height = 560 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                AssertIconAction(window, "NewConsoleButton", "Open a new console",
                    "Open a plain shell terminal (not an agent)");
                AssertIconAction(window, "OpenRepoButton", "Open another repository",
                    "Open another repo (with its own .styloagent/) as a federated instance");
                AssertIconAction(window, "TidyLayoutButton", "Tidy the document layout",
                    "Close empty document areas and reflow the layout");
                AssertIconAction(window, "RosterToggleButton", "Collapse roster", "Collapse roster");
                AssertIconAction(window, "SidePanelToggleButton", "Collapse side panel", "Collapse side panel");
                AssertIconAction(window, "SettingsButton", "Settings", "Settings");

                window.Close();
            }
            finally
            {
                vm?.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
    }

    private static void AssertIconAction(Window window, string name, string automationName, string tooltip)
    {
        var control = window.FindControl<Button>(name);
        Assert.NotNull(control);
        Assert.Equal(automationName, AutomationProperties.GetName(control));
        Assert.Equal(tooltip, ToolTip.GetTip(control));
        Assert.Contains(control!.GetVisualDescendants().OfType<SymbolIcon>(), _ => true);
        Assert.DoesNotContain(control.GetVisualDescendants().OfType<TextBlock>(), _ => true);
    }
}
