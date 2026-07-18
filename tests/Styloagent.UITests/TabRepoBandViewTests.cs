using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Sessions;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Operator: a repo-colour band on the agent tab header so mixed-repo tabs are distinguishable. Renders a
/// two-repo shell and asserts a stripe coloured by the repo hue materializes in the document tab strip.
/// </summary>
[Collection("Avalonia")]
public class TabRepoBandViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public TabRepoBandViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class NewPtyLauncher : IPtyLauncher
    {
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
            => Task.FromResult<IPtySession>(new FakePtySession());
    }

    private sealed class NoWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    [Fact]
    public Task Agent_tab_shows_a_repo_coloured_band_in_a_multi_repo_workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "sty-band-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "saved-context"));
        File.WriteAllText(Path.Combine(root, "saved-context", "overview-context.md"), "# overview");
        return _fx.DispatchAsync(async () =>
        {
            MainWindowViewModel? vm = null;
            try
            {
                vm = await MainWindowViewModel.InitializeAsync(root, new NewPtyLauncher(), new NoWatcher());
                vm.SetReposFromOverviews(new[] { new RepoOverview(
                    "overview-", "/ws/styloagent", "/ws/styloagent/.styloagent/system-prompt.md", 0, "#4CDB6E", true) });
                var second = new RepoOverview(
                    "styloissues-", "/ws/styloissues", "/ws/styloissues/.styloagent/system-prompt.md", 1, "#C77DFF", false);
                vm.AddWorkspaceRepo(second);
                vm.AddRepoOverview(second);   // adds the styloissues- pane as a docked tab

                var window = new MainWindow { DataContext = vm, Width = 1000, Height = 560 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await ScreenshotCapture.CaptureWindowAsync(window, "/tmp/sty-tabband.png", settle: true);

                var tabStrip = window.GetVisualDescendants().FirstOrDefault(d => d.GetType().Name == "DocumentTabStrip");
                Assert.NotNull(tabStrip);

                // The repo band: a Border in the tab strip painted with the styloissues repo hue.
                var wanted = Color.Parse("#C77DFF");
                var band = ((Visual)tabStrip!).GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Background is ISolidColorBrush s && s.Color == wanted);
                Assert.NotNull(band);

                window.Close();
            }
            finally { vm?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });
    }
}
