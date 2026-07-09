using Avalonia.Controls;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.Dock;
using Styloagent.App.Views;
using Styloagent.App.ViewModels;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;

namespace Styloagent.UITests;

/// <summary>
/// Tests for the Dock shell layout and AgentPaneView wiring.
///
/// NOTE: Headless Avalonia cannot realize DataTemplate children, so the
/// MainWindow/AgentPaneView tests assert on logical/data-model structure.
/// The new Dock-model tests are pure model tests and don't need the headless session.
/// </summary>
[Collection("Avalonia")]
public class ShellLayoutTests
{
    private readonly HeadlessAvaloniaFixture _fx;

    public ShellLayoutTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AgentManifestEntry MakeEntry() => new(
        Prefix: "test-",
        Repo: "/repo",
        Worktree: "/repo/wt-test",
        LaunchPromptPath: "",
        RestartPromptPath: "",
        SavedContextPath: "/ctx.md",
        Transport: AgentTransport.Local);

    private static AgentPaneViewModel MakeVm(FakePtyLauncher? launcher = null)
    {
        var entry = MakeEntry();
        launcher ??= new FakePtyLauncher();
        var session = new AgentSession(entry, launcher, new FakeFileWatcher());
        return new AgentPaneViewModel(session, entry, "Test Agent", "#E57373");
    }

    // ── Dock model tests (no headless Avalonia needed) ────────────────────────

    /// <summary>
    /// The factory creates a layout with a DocumentDock that holds the AgentPaneViewModel
    /// as the context of the first Document dockable.
    /// </summary>
    [Fact]
    public void DockLayout_Has_DocumentDock_With_AgentPane()
    {
        var paneVm = MakeVm();
        var factory = new StyloagentDockFactory(paneVm);
        var layout = factory.CreateLayout();
        factory.InitLayout(layout);

        // Centre is a document-only layout: RootDock → DocumentDock (roster + bus are Grid panels).
        var rootDock = Assert.IsType<RootDock>(layout);
        var documentDock = rootDock.VisibleDockables?.OfType<DocumentDock>().FirstOrDefault();
        Assert.NotNull(documentDock);

        var doc = documentDock!.VisibleDockables?.OfType<Document>().FirstOrDefault();
        Assert.NotNull(doc);
        Assert.Same(paneVm, doc!.Context);
        Assert.IsType<AgentPaneViewModel>(doc.Context);
    }

    /// <summary>
    /// The first agent document is active + floatable, so it can be torn out to its own window.
    /// </summary>
    [Fact]
    public void DockLayout_FirstAgentDocument_IsActive_AndFloatable()
    {
        var paneVm = MakeVm();
        var factory = new StyloagentDockFactory(paneVm);
        var layout = factory.CreateLayout();
        factory.InitLayout(layout);

        var documentDock = factory.DocumentDock;
        Assert.NotNull(documentDock);
        var doc = Assert.IsType<Document>(documentDock!.ActiveDockable);
        Assert.Same(paneVm, doc.Context);
        Assert.True(doc.CanFloat);
    }

    /// <summary>
    /// With no agent pane (empty channel), the DocumentDock exists but holds no documents.
    /// </summary>
    [Fact]
    public void DockLayout_WithNoAgent_HasEmptyDocumentDock()
    {
        var factory = new StyloagentDockFactory();
        var layout = factory.CreateLayout();
        factory.InitLayout(layout);

        var rootDock = Assert.IsType<RootDock>(layout);
        var documentDock = rootDock.VisibleDockables?.OfType<DocumentDock>().FirstOrDefault();
        Assert.NotNull(documentDock);
        Assert.Empty(documentDock!.VisibleDockables!.OfType<Document>());
    }

    // ── AgentPaneView ─────────────────────────────────────────────────────────

    /// <summary>
    /// AgentPaneView contains the expected named controls.
    /// </summary>
    [Fact]
    public Task AgentPaneView_Has_Expected_Named_Controls()
    {
        return _fx.DispatchAsync(async () =>
        {
            var paneVm = MakeVm();
            var view = new AgentPaneView { DataContext = paneVm };

            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.NotNull(view.FindControl<Border>("PaneBorder"));
            Assert.NotNull(view.FindControl<Button>("SpawnButton"));
            Assert.NotNull(view.FindControl<Button>("DehydrateButton"));
            Assert.NotNull(view.FindControl<Button>("RehydrateButton"));
            Assert.NotNull(view.FindControl<Button>("RenameButton"));
            Assert.NotNull(view.FindControl<Styloagent.Terminal.TerminalControl>("Terminal"));

            window.Close();
        });
    }

    /// <summary>
    /// AgentPaneView wires TerminalControl when PtyStarted fires via the VM.
    /// Exercises the full pipeline: SpawnCommand → PtyStarted → Attach → Output → render-rows.
    /// Asserts on TerminalControl.Rows (data model) rather than realized TextBlocks because
    /// headless Avalonia without Skia cannot realize DataTemplate children.
    /// </summary>
    [Fact]
    public Task AgentPaneView_Attaches_Terminal_When_PtyStarted()
    {
        return _fx.DispatchAsync(async () =>
        {
            var launcher = new FakePtyLauncher();
            var paneVm = MakeVm(launcher);
            var view = new AgentPaneView { DataContext = paneVm };

            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            // Drain layout so DataContext wiring and OnDataContextChanged complete.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var terminal = view.FindControl<Styloagent.Terminal.TerminalControl>("Terminal");
            Assert.NotNull(terminal);

            // Spawn the session — fires PtyStarted, which posts AttachTerminal to the UI thread.
            await paneVm.SpawnAsync();

            // Drain the UI thread so the Post(AttachTerminal) callback has executed.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // The launcher produced exactly one PTY session.
            Assert.Single(launcher.Spawned);
            var fakePty = launcher.Spawned[0];

            // Emit output through the attached PTY — proves Attach wired the Output event.
            // FireOutput raises IPtySession.Output synchronously on this thread (background-thread
            // simulation); the handler posts RebuildRows to the UI thread via Dispatcher.Post(Render).
            fakePty.FireOutput("HELLO_ATTACH");

            // Drain the Render-priority post so RebuildRows has updated terminal.Rows.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Assert the rendered text contains the emitted string — proves the full
            // PtyStarted → Attach → Output → render-rows pipeline is wired end to end.
            Assert.Contains("HELLO_ATTACH", terminal!.RenderedText);

            window.Close();
        });
    }

    /// <summary>
    /// AgentPaneView applies the border colour from BorderColorHex on the DataContext.
    /// After DataContext is set, the AccentStripe Border.Background should be a SolidColorBrush.
    /// </summary>
    [Fact]
    public Task AgentPaneView_Applies_BorderColorHex()
    {
        return _fx.DispatchAsync(async () =>
        {
            var entry = MakeEntry();
            var session = new AgentSession(entry, new FakePtyLauncher(), new FakeFileWatcher());
            var paneVm = new AgentPaneViewModel(session, entry, "Coloured Agent", "#4FC3F7");

            var view = new AgentPaneView { DataContext = paneVm };
            var window = new Window { Content = view, Width = 800, Height = 600 };
            window.Show();

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // PaneBorder still exists; the accent stripe is now a named inner border.
            var border = view.FindControl<Border>("PaneBorder");
            Assert.NotNull(border);

            var accentStripe = view.FindControl<Border>("AccentStripe");
            Assert.NotNull(accentStripe);
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(accentStripe!.Background);

            window.Close();
        });
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>Fake PTY launcher that records spawned sessions.</summary>
internal sealed class FakePtyLauncher : IPtyLauncher
{
    public List<FakePtySession> Spawned { get; } = new();

    public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
    {
        var s = new FakePtySession();
        Spawned.Add(s);
        return Task.FromResult<IPtySession>(s);
    }
}

/// <summary>Fake file watcher that always acks immediately.</summary>
internal sealed class FakeFileWatcher : IFileWatcher
{
    public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult(true);
}
