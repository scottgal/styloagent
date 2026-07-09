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
            // App.axaml provides FluentTheme + the Dock theme + the DataTemplates; the test app
            // has none of them, so add them here to match the real app exactly.
            window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Styloagent.App/"))
            {
                Source = new Uri("avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml"),
            });
            window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
            window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            pty.FireOutput("STYLOAGENT IN DOCK\r\n> is the terminal visible in the dock?\r\n 1. Yes\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Diagnostic: is the pane/terminal actually in the rendered visual tree, or did the
            // DockControl render nothing?
            var descendants = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToList();
            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"Panes.Count: {vm.Panes.Count}");
            summary.AppendLine($"SelectedPane null? {vm.SelectedPane is null}");
            summary.AppendLine($"Layout null? {vm.Layout is null}");
            summary.AppendLine($"total visuals: {descendants.Count}");
            summary.AppendLine($"AgentPaneView present: {descendants.Any(d => d.GetType().Name == "AgentPaneView")}");
            summary.AppendLine($"TerminalControl present: {descendants.Any(d => d.GetType().Name == "TerminalControl")}");
            summary.AppendLine($"DockControl present: {descendants.Any(d => d.GetType().Name == "DockControl")}");
            summary.AppendLine("types: " + string.Join(",", descendants.Select(d => d.GetType().Name).Distinct().OrderBy(s => s)));
            System.IO.File.WriteAllText("/tmp/styloagent-tree.txt", summary.ToString());

            await ScreenshotCapture.CaptureWindowAsync(window, path);

            // End-to-end typing IN THE REAL LAYOUT: focus the terminal and inject real key input.
            var term = (Avalonia.Controls.Control?)descendants.FirstOrDefault(d => d.GetType().Name == "TerminalControl");
            term!.Focus();
            Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(window, "1");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            window.Close();
        });

        Assert.True(System.IO.File.Exists(path), "mainwindow screenshot should be written");
        // The terminal is in the rendered tree AND typing reaches the agent's PTY.
        Assert.Contains("1", pty.Writes);
        Assert.Contains("\r", pty.Writes);
    }
}
