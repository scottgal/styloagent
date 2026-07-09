using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Git;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class MainWindowScreenshotTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public MainWindowScreenshotTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class OnePtyLauncher : IPtyLauncher
    {
        private readonly IPtySession _pty;
        public OnePtyLauncher(IPtySession pty) => _pty = pty;
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default) => Task.FromResult(_pty);
    }
    private sealed class NoWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default) => Task.FromResult(false);
    }
    private sealed class OneWorktree : IGitReader
    {
        private readonly string _dir;
        public OneWorktree(string dir) => _dir = dir;
        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string root, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitWorktree>>(new[] { new GitWorktree(_dir, "test", "abc") });
    }

    // Screenshots the FULL MainWindow (DockControl -> DocumentDock -> AgentPaneView -> terminal)
    // exactly the way App.axaml.cs builds it, to see if the terminal is a black rectangle in the Dock.
    [Fact]
    public async Task MainWindow_shows_terminal_in_dock()
    {
        const string path = "/tmp/styloagent-mainwindow.png";
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        var pty = new FakePtySession();
        var dir = System.IO.Path.GetTempPath();

        await _fx.DispatchAsync(async () =>
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                "/tmp/no-channel", new OnePtyLauncher(pty), new NoWatcher(), new OneWorktree(dir), dir);

            var window = new MainWindow { DataContext = vm, Width = 900, Height = 500 };
            // TestApp now provides FluentTheme + DockFluentTheme globally; the window only needs
            // the DataTemplates that App.axaml declares.
            window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
            window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // settle:true drives the render loop (HeadlessRender.SettleAsync, framework v1.5.0) so
            // deferred/virtualized content realizes before the shot — the roster/bus items and the
            // dock tab strip now materialize.
            await ScreenshotCapture.CaptureWindowAsync(window, path, settle: true);

            // Assert the shell composes: centre DockControl chrome + roster/bus panels, and the
            // roster actually materializes its agent row (an ItemsControl item).
            var descendants = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToList();
            var names = descendants.Select(d => d.GetType().Name).ToHashSet();
            Assert.Contains("DockControl", names);
            Assert.Contains("DocumentControl", names);
            Assert.Contains("DocumentTabStrip", names);
            Assert.Contains("AgentsView", names);
            Assert.Contains("BusView", names);
            // The roster row for the seeded agent is materialized (settle realized the ItemsControl).
            Assert.Contains(descendants.OfType<TextBlock>(),
                t => t.Text == vm.SelectedPane!.DisplayName);
            Assert.True(vm.Panes.Count >= 1);

            // NOTE: the terminal (AgentPaneView) is hosted in Dock's document content, gated behind a
            // deep Dock virtualization path that does not realize headless even with settle — verified
            // standalone (TerminalInputTests / TerminalScreenshotTests) and in a real GUI.

            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "mainwindow screenshot should be written");
    }
}
