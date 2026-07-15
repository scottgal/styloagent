using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Channel;
using Styloagent.Core.Git;
using Styloagent.Core.Issues;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;
using Styloagent.Core.Router;
using Styloagent.Core.Sessions;
using Styloagent.Git;
using Styloagent.Git.Vendored.Models;
using Styloagent.Terminal;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Generates the figures embedded in the user manual (<c>docs/manual/</c>), straight from the real
/// controls via the UITesting framework — the same headless-render → PNG approach the README uses
/// (<see cref="ReadmeScreenshotTests"/>), so the manual always reflects the actual UI. Running the
/// UITests refreshes <c>docs/manual/images/*.png</c>.
/// </summary>
[Collection("Avalonia")]
public class ManualScreenshotTests
{
    // The channel prefixes used across the manual's mock fleet — same colour-by-prefix scheme the
    // roster and bus use, so a "foss-" row is the same hue everywhere in the manual.
    private static readonly string[] TeamPrefixes = { "foss-", "dash-", "bus-", "docs-", "caps-", "deploy-", "mae-" };

    private readonly HeadlessAvaloniaFixture _fx;
    public ManualScreenshotTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    /// <summary>Resolves (and creates) <c>docs/manual/images/</c> by walking up to the repo root.</summary>
    private static string ShotDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Styloagent.App")))
            dir = dir.Parent;
        string root = dir?.FullName ?? Directory.GetCurrentDirectory();
        string shots = Path.Combine(root, "docs", "manual", "images");
        Directory.CreateDirectory(shots);
        return shots;
    }

    private static string Shot(string name) => Path.Combine(ShotDir(), name);

    // ── Onboarding: the welcome / open-a-project screen ──────────────────────────────────────────
    [Fact]
    public Task Manual_welcome()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml", new NullPicker(), _ => { });
            vm.Recent.Add("/Users/you/RiderProjects/styloagent");
            vm.Recent.Add("/Users/you/work/atoms");
            vm.Recent.Add("/Users/you/scratch/prototype");

            var view = new WelcomeView { DataContext = vm };
            var window = new Window { Width = 520, Height = 380, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);
            await ScreenshotCapture.CaptureControlAsync(window, view, Shot("welcome.png"));
            window.Close();
        });
    }

    // ── The cockpit shell (full window) ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Manual_cockpit()
    {
        var pty = new FakePtySession();
        // Isolated empty repo root — NOT Path.GetTempPath() (DocLibrary recursively scans repoRoot).
        var dir = Path.Combine(Path.GetTempPath(), "manual-cockpit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await _fx.DispatchAsync(async () =>
            {
                var vm = await MainWindowViewModel.InitializeAsync(
                    "/tmp/no-channel", new OneShotLauncher(pty), new NoOpWatcher(), new OneWorktreeReader(dir), dir);

                var window = new MainWindow { DataContext = vm, Width = 1180, Height = 700 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureWindowAsync(window, Shot("cockpit.png"), settle: true);
                window.Close();
                vm.Dispose();
            });
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── Roster incl. the PROPOSED section ────────────────────────────────────────────────────────
    [Fact]
    public Task Manual_roster_and_proposed()
    {
        var pty = new FakePtySession();
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "manual-proposed-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Scaffold a real project, then write the proposals the overview agent would produce.
                var cfg = ProjectScaffolder.Ensure(root);
                File.WriteAllText(cfg.ProposedAgentsPath,
                    "agents:\n" +
                    "  - prefix: foss-\n    responsibility: owns the FOSS packages & releases\n    dir: .\n    launchPrompt: |\n      You are foss-.\n" +
                    "  - prefix: dash-\n    responsibility: the cockpit dashboard & layout\n    dir: .\n    launchPrompt: |\n      You are dash-.\n" +
                    "  - prefix: bus-\n    responsibility: the signal bus & message routing\n    dir: .\n    launchPrompt: |\n      You are bus-.\n" +
                    "  - prefix: docs-\n    responsibility: the user manual & documentation\n    dir: .\n    launchPrompt: |\n      You are docs-.\n" +
                    "  - prefix: git-\n    responsibility: the embedded git client & worktrees\n    dir: .\n    launchPrompt: |\n      You are git-.\n");

                var vm = await MainWindowViewModel.InitializeAsync(
                    cfg.ChannelRoot, new OneShotLauncher(pty), new NoOpWatcher(), repoRoot: cfg.Root);
                vm.AttachProject(cfg);

                var view = new AgentsView { DataContext = vm };
                var window = new Window { Width = 300, Height = 360, Content = view };
                window.Show();

                // The PROPOSED ItemsControl materializes its cards asynchronously — poll until the
                // first card's prefix text appears rather than relying on a single settle.
                bool HasCards() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "") == "foss-");
                for (int i = 0; i < 40 && !HasCards(); i++)
                {
                    await HeadlessRender.SettleAsync(window);
                    await Task.Delay(25);
                }
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("roster-proposed.png"));
                window.Close();
                vm.Dispose();
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });
    }

    // ── A live agent pane: lifecycle toolbar (Spawn/Dehydrate/Rehydrate) + colour terminal ───────
    [Fact]
    public Task Manual_agent_pane_lifecycle()
    {
        return _fx.DispatchAsync(async () =>
        {
            var pty = new FakePtySession();
            // A non-empty SavedContextPath makes Dehydrate available once the session is Live, so the
            // toolbar reads as a real running agent (Spawn disabled, Dehydrate enabled).
            var ctx = Path.Combine(Path.GetTempPath(), "manual-ctx-" + Guid.NewGuid().ToString("N") + ".md");
            var manifest = new AgentManifestEntry(
                "foss-", "", Path.GetTempPath(), "", "", ctx, AgentTransport.Local);
            var session = new AgentSession(manifest, new OneShotLauncher(pty), new NoOpWatcher());
            var vm = new AgentPaneViewModel(session, manifest, "foss", "#57A64A");
            var view = new AgentPaneView { DataContext = vm };
            var window = new Window { Width = 720, Height = 380, Content = view };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Spawn: raises PtyStarted -> the view attaches the TerminalControl to the pty.
            await vm.SpawnCommand.ExecuteAsync(null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            pty.FireOutput(
                "[38;2;215;119;87m✻ Welcome to Claude Code[0m\r\n\r\n" +
                "[90m  cwd: ~/RiderProjects/styloagent/.worktrees/foss[0m\r\n\r\n" +
                "[32m●[0m Reading [1msrc/Styloagent.Core/Router/RouterModel.cs[0m\r\n" +
                "[32m●[0m Editing [1mREADME.md[0m [32m+12[0m [31m-3[0m\r\n" +
                "[33m●[0m Running [1mdotnet test[0m … [32m248 passed[0m\r\n" +
                "[44m 1. Yes, commit it [0m   [90m2. No, keep editing[0m\r\n");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ScreenshotCapture.CaptureControlAsync(window, view, Shot("agent-pane.png"));
            window.Close();
        });
    }

    // ── Signal Bus: attention-first threads ──────────────────────────────────────────────────────
    [Fact]
    public Task Manual_signal_bus()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "manual-bus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            Directory.CreateDirectory(Path.Combine(root, "archive", "inbox"));
            try
            {
                string Ago(TimeSpan t) => DateTimeOffset.UtcNow.Subtract(t).ToString("O");
                // Unreplied → pinned under "Needs attention".
                File.WriteAllText(Path.Combine(root, "inbox", "foss-release-cut.md"),
                    $"**From:** deploy-\n**Timestamp:** {Ago(TimeSpan.FromMinutes(2))}\n\nCut the 2.9 release?");
                // Replied thread → falls to "Recent".
                File.WriteAllText(Path.Combine(root, "inbox", "dash-layout-fix.md"),
                    $"**From:** mae-\n**Timestamp:** {Ago(TimeSpan.FromMinutes(6))}\n\nAuto-tile tweak landed — take a look.");
                File.WriteAllText(Path.Combine(root, "outbox", "dash-layout-fix.reply.md"),
                    $"**From:** dash-\n**Timestamp:** {Ago(TimeSpan.FromMinutes(5))}\n\nLooks good, merged.");
                // Archived.
                File.WriteAllText(Path.Combine(root, "archive", "inbox", "caps-pkg-split.md"),
                    $"**From:** foss-\n**Timestamp:** {Ago(TimeSpan.FromHours(3))}\n\nSplit the LucidView packages.");

                var vm = new BusViewModel(root, TeamPrefixes, new ChannelProjection());
                await vm.LoadAsync();
                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 340, Height = 460, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("signal-bus.png"));
                window.Close();
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });
    }

    // ── Activity Timeline: a merged, newest-first operations feed ─────────────────────────────────
    [Fact]
    public Task Manual_activity_timeline()
    {
        return _fx.DispatchAsync(async () =>
        {
            var vm = new TimelineViewModel();
            // Added oldest-first; each Add inserts at the head, so the newest ends up on top.
            var now = DateTimeOffset.Now;
            void Op(int secsAgo, string agent, string desc, string hex, string? path = null)
                => vm.Add(now.AddSeconds(-secsAgo), agent, desc, hex, path);

            Op(240, "docs-", "reading · README.md", "#4FA3D1", "README.md");
            Op(210, "foss-", "→ deploy- · Cut the 2.9 release?", "#57A64A");
            Op(180, "bus-",  "editing · ChannelProjection.cs", "#C77DFF", "src/Styloagent.Core/Channel/ChannelProjection.cs");
            Op(120, "dash-", "running · dotnet test", "#E5A05A");
            Op(90,  "dash-", "idle", "#E5A05A");
            Op(45,  "docs-", "editing · docs/manual/README.md", "#4FA3D1", "docs/manual/README.md");
            Op(12,  "foss-", "dehydrated", "#57A64A");

            var view = new TimelineView { DataContext = vm };
            var window = new Window { Width = 360, Height = 300, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);
            await ScreenshotCapture.CaptureControlAsync(window, view, Shot("activity-timeline.png"));
            window.Close();
        });
    }

    // ── Document Library: the file/folder tree of docs + channel messages ────────────────────────
    [Fact]
    public Task Manual_document_library()
    {
        return _fx.DispatchAsync(async () =>
        {
            var repo = Path.Combine(Path.GetTempPath(), "manual-repo-" + Guid.NewGuid().ToString("N"));
            var chan = Path.Combine(Path.GetTempPath(), "manual-chan-" + Guid.NewGuid().ToString("N"));
            // Flat tree: files directly under each source root, no sub-folders. The DocLibrary TreeView
            // auto-expands every folder, and a folder node that itself contains a sub-folder collapses to
            // ~zero height under headless render — the sub-tree then paints on top of its parent (garbled,
            // overlapping labels), a headless-only artifact no amount of settling clears. A flat file list
            // under the two source roots (repo / channel) renders cleanly and is representative of the panel.
            Directory.CreateDirectory(repo);
            Directory.CreateDirectory(chan);
            try
            {
                File.WriteAllText(Path.Combine(repo, "README.md"), "# readme");
                File.WriteAllText(Path.Combine(repo, "ARCHITECTURE.md"), "# architecture");
                File.WriteAllText(Path.Combine(repo, "CONTRIBUTING.md"), "# contributing");
                File.WriteAllText(Path.Combine(chan, "PROTOCOL.md"), "# protocol");
                File.WriteAllText(Path.Combine(chan, "foss-context.md"), "# ctx");

                var vm = new DocLibraryViewModel(repo, chan, _ => { });
                var view = new DocLibraryView { DataContext = vm };
                var window = new Window { Width = 320, Height = 460, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("document-library.png"));
                window.Close();
            }
            finally
            {
                if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true);
                if (Directory.Exists(chan)) Directory.Delete(chan, recursive: true);
            }
        });
    }

    // ── Git panel: the vendored commit graph ─────────────────────────────────────────────────────
    [Fact]
    public Task Manual_git_panel()
    {
        return _fx.DispatchAsync(async () =>
        {
            ulong T(int daysAgo) => (ulong)DateTimeOffset.Now.AddDays(-daysAgo).ToUnixTimeSeconds();
            var fakeLog = new FakeGitLog(
                new Commit { SHA = "d5e4a0c1", Subject = "feat(fleet): per-fleet permission mode", Parents = ["4c3a7150"], CommitterTime = T(0) },
                new Commit { SHA = "4c3a7150", Subject = "fix(fleet): spawned agents can be dehydrated", Parents = ["ddb84cf0"], CommitterTime = T(0) },
                new Commit { SHA = "ddb84cf0", Subject = "test(bus): de-flake BusViewModel tests", Parents = ["a6e8a520"], CommitterTime = T(1) },
                new Commit { SHA = "a6e8a520", Subject = "fix(fleet): the overview OWNS its agents", Parents = ["a5b7ee10"], CommitterTime = T(1) },
                new Commit { SHA = "a5b7ee10", Subject = "release: add arm64 win/linux builds", Parents = ["b0c1d2e3"], CommitterTime = T(2) },
                new Commit { SHA = "b0c1d2e3", Subject = "docs: user manual + generated figures", Parents = [], CommitterTime = T(3) }
            );

            var vm = new GitGraphViewModel(fakeLog);
            await vm.LoadAsync("/manual/worktree");

            var view = new GitGraphView { DataContext = vm };
            var window = new Window { Width = 380, Height = 300, Content = view };
            window.Show();
            await HeadlessRender.SettleAsync(window);
            await ScreenshotCapture.CaptureControlAsync(window, view, Shot("git-panel.png"));
            window.Close();
        });
    }

    // ── Router: the resource-lease ledger ────────────────────────────────────────────────────────
    [Fact]
    public Task Manual_router()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "manual-router-" + Guid.NewGuid().ToString("N"));
            try
            {
                var now = DateTimeOffset.UtcNow;
                // A prod account resource, currently held by deploy-, with a queue behind it.
                var resDir = Path.Combine(root, "prod", "accounts", "deploy-key");
                Directory.CreateDirectory(resDir);
                File.WriteAllText(Path.Combine(resDir, "resource.yaml"), "capacity: 1\nleaseTtl: 10m\n");
                RouterWriter.WriteGrant(root, "prod", ResourceKind.Account, "deploy-key",
                    "deploy-", now - TimeSpan.FromSeconds(30), now + TimeSpan.FromMinutes(9), now - TimeSpan.FromSeconds(60));
                RouterClient.DropClaim(root, "prod", "deploy-key", "foss-", "cut the 2.9 release", now);

                // A free staging slot resource (no holder).
                var slotDir = Path.Combine(root, "staging", "slots", "browser-1");
                Directory.CreateDirectory(slotDir);
                File.WriteAllText(Path.Combine(slotDir, "resource.yaml"), "capacity: 2\nleaseTtl: 5m\n");

                var vm = new RouterViewModel(root);
                vm.Refresh();
                var view = new RouterView { DataContext = vm };
                var window = new Window { Width = 360, Height = 320, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("router.png"));
                window.Close();
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });
    }

    // ── Issues: the fleet's reported issues ──────────────────────────────────────────────────────
    [Fact]
    public Task Manual_issues()
    {
        return _fx.DispatchAsync(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "manual-issues-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var now = DateTimeOffset.Now;
                IssueStore.Write(dir, "dash-", "Terminal pane scroll jumps to top on output", "", "high", now);
                IssueStore.Write(dir, "bus-", "Delivery is flaky when recipient is dehydrated", "", "medium", now.AddMinutes(-20));
                IssueStore.Write(dir, "docs-", "Some README screenshots have no generator test", "", "low", now.AddHours(-1));

                var vm = new IssuesViewModel(dir);
                var view = new IssuesView { DataContext = vm };
                var window = new Window { Width = 360, Height = 300, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("issues.png"));
                window.Close();
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        });
    }

    // ── Test doubles (mirror the ones in ReadmeScreenshotTests / PaneScreenshotTests) ─────────────

    private sealed class NullPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class OneShotLauncher : IPtyLauncher
    {
        private readonly IPtySession _pty;
        public OneShotLauncher(IPtySession pty) => _pty = pty;
        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default) => Task.FromResult(_pty);
    }

    private sealed class NoOpWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class OneWorktreeReader : IGitReader
    {
        private readonly string _dir;
        public OneWorktreeReader(string dir) => _dir = dir;
        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string root, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitWorktree>>(new[] { new GitWorktree(_dir, "foss", "abc123") });
    }

    private sealed class FakeGitLog : IGitLog
    {
        private readonly IReadOnlyList<Commit> _commits;
        public FakeGitLog(params Commit[] commits) => _commits = commits;
        public Task<GitResult<IReadOnlyList<Commit>>> GetCommitsAsync(
            string worktreePath, int limit = 200, CancellationToken ct = default)
            => Task.FromResult(GitResult<IReadOnlyList<Commit>>.Success(_commits));
    }
}
