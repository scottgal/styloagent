using Avalonia.Controls;
using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.UITests;

/// <summary>
/// Regression for BUG 4 Part B — federated core-/web- terminals show "just a cursor" and never paint.
///
/// Root cause: the centre dock uses Dock's ControlRecycling, so ONE <see cref="AgentPaneView"/> +
/// <see cref="Styloagent.Terminal.TerminalControl"/> instance is cached per agent and only the ACTIVE
/// document's content sits in the tree. A federated pane opens as a BACKGROUND tab, so when its PTY
/// starts the recycled view is detached (its <c>OnDetachedFromLogicalTree</c> ran <c>UnsubscribeVm</c>,
/// dropping the PtyStarted subscription, and <c>Terminal.Detach()</c>). With no re-attach on
/// <c>OnAttachedToLogicalTree</c>, switching to the tab never re-wires the terminal → blank.
///
/// These tests drive the tree lifecycle a dock tab-switch produces on the SAME recycled instance by
/// swapping the window content (detach → re-attach). They assert on <c>TerminalControl.RenderedText</c>
/// (pure data — no Skia) as the other AgentPaneView tests do. The live-PTY / real dock tab-switch part
/// is restart/manual-verified — the headless deferred-content presenter can't be driven (see
/// <see cref="MainWindowDockTests"/>).
/// </summary>
[Collection("Avalonia")]
public class AgentPaneReattachTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AgentPaneReattachTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static AgentManifestEntry MakeEntry() => new(
        Prefix: "core-",
        Repo: "/repo",
        Worktree: "/repo/wt-core",
        LaunchPromptPath: "",
        RestartPromptPath: "",
        SavedContextPath: "/ctx.md",
        Transport: AgentTransport.Local);

    private static AgentPaneViewModel MakeVm(FakePtyLauncher launcher)
    {
        var entry = MakeEntry();
        var session = new AgentSession(entry, launcher, new FakeFileWatcher());
        return new AgentPaneViewModel(session, entry, "Core Agent", "#E57373");
    }

    private static async Task DrainAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    /// <summary>
    /// The federated case: the PTY starts while the pane is a BACKGROUND tab (detached), so the
    /// recycled view misses PtyStarted entirely. Switching to the tab (re-entering the logical tree)
    /// must re-attach to <c>CurrentPty</c> and replay the session backlog so the pane paints — instead
    /// of showing "just a cursor".
    /// </summary>
    [Fact]
    public Task Federated_Pane_Paints_When_Switched_To_After_Pty_Started_While_Backgrounded()
    {
        return _fx.DispatchAsync(async () =>
        {
            var launcher = new FakePtyLauncher();
            var vm = MakeVm(launcher);
            var view = new AgentPaneView { DataContext = vm };

            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();
            await DrainAsync();

            var terminal = view.FindControl<Styloagent.Terminal.TerminalControl>("Terminal");
            Assert.NotNull(terminal);

            // Background the pane BEFORE the PTY exists (a federated tab opens un-selected).
            window.Content = null;
            await DrainAsync();

            // The agent spawns while backgrounded: PtyStarted fires but the detached view is
            // unsubscribed, so it's missed. The output it produces is buffered in the session backlog.
            await vm.SpawnAsync();
            await DrainAsync();
            var pty = Assert.Single(launcher.Spawned);
            pty.SeedBacklog("FEDERATED_BANNER_XYZ");

            // Operator switches to the tab → the recycled view re-enters the logical tree.
            window.Content = view;
            await DrainAsync();

            Assert.Contains("FEDERATED_BANNER_XYZ", terminal!.RenderedText);

            window.Close();
        });
    }

    /// <summary>
    /// The ordinary tab-switch case: a pane that already painted is switched away and back on the SAME
    /// recycled instance. The re-attach must reconstruct the current screen from the backlog WITHOUT
    /// duplicating what the surviving VT engine already holds (Attach replays the whole backlog).
    /// </summary>
    [Fact]
    public Task ReAttach_After_Painting_Does_Not_Duplicate_Buffer()
    {
        return _fx.DispatchAsync(async () =>
        {
            var launcher = new FakePtyLauncher();
            var vm = MakeVm(launcher);
            var view = new AgentPaneView { DataContext = vm };

            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();
            await DrainAsync();

            var terminal = view.FindControl<Styloagent.Terminal.TerminalControl>("Terminal");
            Assert.NotNull(terminal);

            // Spawn while active (primary-pane style): the terminal attaches immediately.
            await vm.SpawnAsync();
            await DrainAsync();
            var pty = Assert.Single(launcher.Spawned);

            // One chunk of output that the real session both DELIVERS live and buffers for replay.
            pty.SeedBacklog("UNIQUE_LINE_QWERTY");
            pty.FireOutput("UNIQUE_LINE_QWERTY");
            await DrainAsync();
            Assert.Equal(1, Occurrences(terminal!.RenderedText, "UNIQUE_LINE_QWERTY"));

            // Switch away (background) then back (foreground) on the SAME recycled instance.
            window.Content = null;
            await DrainAsync();
            window.Content = view;
            await DrainAsync();

            // The re-attach replays the backlog; it must not double the line already on screen.
            Assert.Equal(1, Occurrences(terminal!.RenderedText, "UNIQUE_LINE_QWERTY"));

            window.Close();
        });
    }
}
