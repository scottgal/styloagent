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
using Styloagent.Core.Hooks;
using Styloagent.Core.Projects;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// Generates the demo screenshots embedded in the README, straight from the real controls via the
/// UITesting framework (ScreenshotCapture + HeadlessRender). Running the UITests refreshes
/// <c>docs/screenshots/*.png</c> so the README always reflects the actual UI.
/// </summary>
[Collection("Avalonia")]
public class ReadmeScreenshotTests
{
    private static readonly string[] BusPrefixes = { "foss-", "dash-", "caps-", "deploy-", "mae-" };

    private readonly HeadlessAvaloniaFixture _fx;
    public ReadmeScreenshotTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private static string ShotDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Styloagent.App")))
            dir = dir.Parent;
        string root = dir?.FullName ?? Directory.GetCurrentDirectory();
        string shots = Path.Combine(root, "docs", "screenshots");
        Directory.CreateDirectory(shots);
        return shots;
    }

    private static string Shot(string name) => Path.Combine(ShotDir(), name);

    [Fact]
    public Task Capture_terminal_colour()
    {
        return _fx.DispatchAsync(async () =>
        {
            var control = new TerminalControl { Width = 620, Height = 240 };
            var fake = new FakePtySession();
            var window = new Window { Width = 640, Height = 260, Content = control };
            window.Show();
            control.Attach(fake);
            fake.FireOutput(
                "\u001b[38;2;215;119;87m✻ Welcome to Claude Code\u001b[0m\r\n\r\n" +
                "\u001b[32m●\u001b[0m Reading \u001b[1msrc/BusThreadClassifier.cs\u001b[0m\r\n" +
                "\u001b[33m●\u001b[0m Running tests… \u001b[32m75 passed\u001b[0m\r\n" +
                "\u001b[44m 1. Yes, proceed \u001b[0m   \u001b[90m2. No, stop\u001b[0m\r\n" +
                "\u001b[31m✗\u001b[0m \u001b[90mdiff:\u001b[0m \u001b[31m- old line\u001b[0m  \u001b[32m+ new line\u001b[0m\r\n");
            await HeadlessRender.SettleAsync(window);
            await ScreenshotCapture.CaptureControlAsync(window, control, Shot("terminal-colour.png"));
            window.Close();
        });
    }

    [Fact]
    public Task Capture_bus_attention()
    {
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "shot-bus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "inbox"));
            Directory.CreateDirectory(Path.Combine(root, "outbox"));
            Directory.CreateDirectory(Path.Combine(root, "archive", "inbox"));
            try
            {
                // Timestamps relative to now so the "N ago" labels always read sensibly.
                string Ago(TimeSpan t) => DateTimeOffset.UtcNow.Subtract(t).ToString("O");
                File.WriteAllText(Path.Combine(root, "inbox", "foss-release-cut.md"),
                    $"**From:** deploy-\n**Timestamp:** {Ago(TimeSpan.FromMinutes(2))}\n\nCut the 2.9 release?");
                File.WriteAllText(Path.Combine(root, "inbox", "dash-layout-fix.md"),
                    $"**From:** mae-\n**Timestamp:** {Ago(TimeSpan.FromMinutes(5))}\n\nLayout tweak.");
                File.WriteAllText(Path.Combine(root, "outbox", "dash-layout-fix.reply.md"),
                    $"**From:** dash-\n**Timestamp:** {Ago(TimeSpan.FromMinutes(4))}\n\nDone.");
                File.WriteAllText(Path.Combine(root, "archive", "inbox", "caps-pkg-split.md"),
                    $"**From:** foss-\n**Timestamp:** {Ago(TimeSpan.FromHours(2))}\n\nSplit packages.");

                var vm = new BusViewModel(root, BusPrefixes, new ChannelProjection());
                await vm.LoadAsync();
                var view = new BusView { DataContext = vm };
                var window = new Window { Width = 340, Height = 460, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("bus-attention.png"));
                window.Close();
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });
    }

    [Fact]
    public Task Capture_doc_library()
    {
        return _fx.DispatchAsync(async () =>
        {
            var repo = Path.Combine(Path.GetTempPath(), "shot-repo-" + Guid.NewGuid().ToString("N"));
            var chan = Path.Combine(Path.GetTempPath(), "shot-chan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            Directory.CreateDirectory(Path.Combine(chan, "saved-context"));
            try
            {
                File.WriteAllText(Path.Combine(repo, "README.md"), "# readme");
                File.WriteAllText(Path.Combine(repo, "docs", "design.md"), "# design");
                File.WriteAllText(Path.Combine(chan, "PROTOCOL.md"), "# protocol");
                File.WriteAllText(Path.Combine(chan, "saved-context", "foss-context.md"), "# ctx");

                var vm = new DocLibraryViewModel(repo, chan, _ => { });
                var view = new DocLibraryView { DataContext = vm };
                var window = new Window { Width = 320, Height = 460, Content = view };
                window.Show();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("doc-library.png"));
                window.Close();
            }
            finally
            {
                if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true);
                if (Directory.Exists(chan)) Directory.Delete(chan, recursive: true);
            }
        });
    }

    [Fact]
    public Task Capture_markdown_doc()
    {
        return _fx.DispatchAsync(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "shot-md-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var mdPath = Path.Combine(dir, "PROTOCOL.md");
            try
            {
                File.WriteAllText(mdPath,
                    "# Signal Bus Protocol\n\n" +
                    "Agents coordinate over a **file-drop** channel. Each message is a markdown file.\n\n" +
                    "## Routing\n\n" +
                    "- `inbox/` — messages awaiting a reply\n" +
                    "- `outbox/` — replies\n" +
                    "- `archive/` — resolved threads\n\n" +
                    "A thread is *replied* once an `outbox/<slug>.reply.md` exists.\n");

                var docVm = new MarkdownDocumentViewModel("PROTOCOL.md", mdPath);
                var view = new MarkdownDocumentView { DataContext = docVm };
                var window = new Window { Width = 560, Height = 400, Content = view };
                window.Show();

                int TextEls() => window.GetVisualDescendants().OfType<TextBlock>().Count();
                for (int i = 0; i < 40 && TextEls() < 1; i++)
                {
                    await HeadlessRender.SettleAsync(window);
                    await Task.Delay(25);
                }
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("markdown-doc.png"));
                window.Close();
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        });
    }

    [Fact]
    public Task Capture_cockpit()
    {
        var pty = new FakePtySession();
        var dir = Path.GetTempPath();
        return _fx.DispatchAsync(async () =>
        {
            var vm = await MainWindowViewModel.InitializeAsync(
                "/tmp/no-channel", new OneShotLauncher(pty), new NoOpWatcher(), new OneWorktreeReader(dir), dir);

            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 640 };
            window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
            window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
            window.Show();
            await HeadlessRender.SettleAsync(window);
            await ScreenshotCapture.CaptureWindowAsync(window, Shot("cockpit.png"), settle: true);
            window.Close();
        });
    }

    [Fact]
    public Task Capture_welcome()
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

    [Fact]
    public Task Capture_proposed_team()
    {
        var pty = new FakePtySession();
        return _fx.DispatchAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "shot-proposed-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Scaffold a real project, then write the proposals the overview agent would produce.
                var cfg = ProjectScaffolder.Ensure(root);
                File.WriteAllText(cfg.ProposedAgentsPath,
                    "agents:\n" +
                    "  - prefix: foss-\n    responsibility: owns the FOSS packages & releases\n    dir: .\n    launchPrompt: |\n      You are foss-.\n" +
                    "  - prefix: dash-\n    responsibility: the cockpit dashboard & layout\n    dir: .\n    launchPrompt: |\n      You are dash-.\n" +
                    "  - prefix: bus-\n    responsibility: the signal bus & message routing\n    dir: .\n    launchPrompt: |\n      You are bus-.\n" +
                    "  - prefix: docs-\n    responsibility: docs, specs & onboarding\n    dir: .\n    launchPrompt: |\n      You are docs-.\n");

                // A cockpit VM against the scaffolded project (empty channel → no live panes yet), then
                // wire the proposed team so the PROPOSED section renders from the yaml above.
                var vm = await MainWindowViewModel.InitializeAsync(
                    cfg.ChannelRoot, new OneShotLauncher(pty), new NoOpWatcher(), repoRoot: cfg.Root);
                vm.AttachProject(cfg);

                var view = new AgentsView { DataContext = vm };
                var window = new Window { Width = 300, Height = 320, Content = view };
                window.Show();

                // The PROPOSED ItemsControl materializes its cards asynchronously — poll until the
                // first card's prefix text appears rather than relying on a single settle.
                bool HasCards() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "") == "foss-");
                for (int i = 0; i < 40 && !HasCards(); i++)
                {
                    await HeadlessRender.SettleAsync(window);
                    await Task.Delay(25);
                }
                await ScreenshotCapture.CaptureControlAsync(window, view, Shot("proposed-team.png"));
                window.Close();
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });
    }

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
}
