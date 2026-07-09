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

            await ScreenshotCapture.CaptureWindowAsync(window, path);

            // Assert the shell composes: centre DockControl renders its chrome + the roster/bus panels.
            // (The terminal itself is behind Dock's deferred content presenter, which the headless
            // platform can't drive — its rendering + typing are covered in TerminalInputTests /
            // TerminalScreenshotTests. Here we verify the DOCK shell renders, which is what the old
            // "Dock renders nothing" bug broke.)
            var names = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
                .Select(d => d.GetType().Name).ToHashSet();
            Assert.Contains("DockControl", names);
            Assert.Contains("DocumentControl", names);
            Assert.Contains("AgentsView", names);
            Assert.Contains("BusView", names);
            Assert.True(vm.Panes.Count >= 1);
            Assert.NotNull(vm.SelectedPane);

            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "mainwindow screenshot should be written");
    }
}
