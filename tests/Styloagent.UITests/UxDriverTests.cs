using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.Threading;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Git;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

// Drives the assembled MainWindow the way a user does — spawn several agents, open a markdown doc —
// at a realistic window size, screenshotting each stage so layout regressions (panes overflowing
// into each other, docs not rendering, controls overlapping) are visible in the captured pixels.
// This is a manual driver harness, not an assertion gate: the screenshots are the deliverable.
// Avalonia-Markdown collection (runs isolated) because LiveMarkdown rendering leaves the shared
// headless dispatcher wedged for later tests in the main collection.
[Collection("Avalonia-Markdown")]
public class UxDriverTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public UxDriverTests(HeadlessAvaloniaFixture fx) => _fx = fx;

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

    private static async Task Settle(Window w)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    // Dumps the realized visual tree to /tmp/ux-diag.txt — which document views materialize, the dock
    // chrome controls present, and every rendered TextBlock's text (so invisible/blank tab labels show
    // up here as present-but-unseen vs genuinely-absent).
    private static readonly string[] ProbeTypes =
    {
        "DocumentControl", "DocumentTabStrip", "DocumentTabStripItem",
        "TabStripItem", "AgentPaneView", "MarkdownDocumentView", "MarkdownScrollViewer",
        "TextBlock",
    };

    private static void Diag(Window w, string stage)
    {
        var d = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).ToList();
        var counts = ProbeTypes.Select(n => $"{n}={d.Count(x => x.GetType().Name == n)}");
        var texts = d.OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t));

        // Bounds of each dock tab item + its label, to reveal zero-width / clipped / overlapping tabs.
        var sb = new System.Text.StringBuilder();
        foreach (var item in d.Where(x => x.GetType().Name == "DocumentTabStripItem").OfType<Visual>())
        {
            var b = item.Bounds;
            var label = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(item)
                .OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
            var lb = label is null ? "no-label" : $"'{label.Text}' @ {label.Bounds} fg={label.Foreground} vis={label.IsVisible} op={label.Opacity}";
            sb.Append($"  tab bounds={b} label=[{lb}]\n");
        }
        // The tab strip container + document control bounds, to see the strip's own geometry.
        foreach (var n in new[] { "DocumentTabStrip", "DocumentControl", "DocumentDock" })
            foreach (var v in d.Where(x => x.GetType().Name == n).OfType<Visual>())
                sb.Append($"  {n} bounds={v.Bounds}\n");

        // What Dock's deferred content host actually holds after an active-dockable change.
        foreach (var dcc in d.Where(x => x.GetType().Name == "DeferredContentControl"))
        {
            var kids = Avalonia.VisualTree.VisualExtensions.GetVisualChildren((Visual)dcc)
                .Select(k => k.GetType().Name).ToList();
            var dc = (dcc as Control)?.DataContext?.GetType().Name ?? "null";
            var contentProp = dcc.GetType().GetProperty("Content")?.GetValue(dcc)?.GetType().Name ?? "n/a";
            sb.Append($"  DeferredContentControl dc={dc} Content={contentProp} kids=[{string.Join(",", kids)}]\n");
        }

        var line = $"=== {stage} ===\ncounts: {string.Join(", ", counts)}\ntexts: {string.Join(" | ", texts)}\n{sb}\n";
        System.IO.File.AppendAllText("/tmp/ux-diag.txt", line);
    }

    [Fact]
    public async Task Drive_multiple_agents_and_a_doc()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sty-ux-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var docPath = System.IO.Path.Combine(dir, "notes.md");
        System.IO.File.WriteAllText(docPath,
            "# Notes\n\nThis is a **markdown** document.\n\n- one\n- two\n- three\n\n```csharp\nvar x = 1;\n```\n");

        try
        {
            await _fx.DispatchAsync(async () =>
            {
                var pty = new FakePtySession();
                var vm = await MainWindowViewModel.InitializeAsync(
                    "/tmp/no-channel", new OnePtyLauncher(pty), new NoWatcher(), new OneWorktree(dir), dir);

                var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
                // App.axaml requests the Dark variant globally; TestApp does not. Match it so the Dock
                // tab strip renders with the same (dark) chrome the real app uses.
                window.RequestedThemeVariant = ThemeVariant.Dark;
                // Mirror App.axaml's Application.DataTemplates so the assembled window resolves the same
                // views the real app does (App.axaml isn't loaded under TestApp).
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<DiagramDocumentViewModel>((_, _) => new DiagramDocumentView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<MarkdownDocumentViewModel>((_, _) => new MarkdownDocumentView(), true));
                window.Show();
                await Settle(window);

                int Count(string typeName) => Avalonia.VisualTree.VisualExtensions
                    .GetVisualDescendants(window).Count(x => x.GetType().Name == typeName);
                // Loop-settle until a control type materializes (LiveMarkdown / Dock deferred content
                // realize asynchronously — the framework's MarkdownDocumentRenderTests uses this pattern).
                async Task SettleUntil(string typeName, int maxPasses = 40)
                {
                    for (int i = 0; i < maxPasses && Count(typeName) < 1; i++)
                    {
                        await HeadlessRender.SettleAsync(window);
                        await Task.Delay(25);
                    }
                }

                // Stage 1: three agents in the dock (reproduces "spawn overflows into previous").
                vm.AddAgent();
                vm.AddAgent();
                await HeadlessRender.SettleAsync(window);
                await ScreenshotCapture.CaptureWindowAsync(window, "/tmp/ux-3agents.png", settle: true);
                Diag(window, "AFTER 3 AGENTS");

                // Stage 2: open a markdown document (reproduces "markdown docs don't open").
                vm.OpenMarkdownDocument(new MarkdownDocumentViewModel("notes.md", docPath));
                await SettleUntil("MarkdownDocumentView");
                await ScreenshotCapture.CaptureWindowAsync(window, "/tmp/ux-doc.png", settle: true);
                Diag(window, "AFTER OPEN DOC (loop-settled)");

                window.Close();
                vm.Dispose();
            });
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    // Renders MarkdownDocumentView STANDALONE (outside the Dock, which does not realize document
    // content headless) to prove the markdown view itself renders its text — isolating "docs don't
    // open" between the view (this test) and the dock's content hosting (real-app only).
    [Fact]
    public async Task Markdown_view_renders_standalone()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sty-md-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var docPath = System.IO.Path.Combine(dir, "notes.md");
        System.IO.File.WriteAllText(docPath,
            "# Notes Heading\n\nThis is a **markdown** paragraph with body text.\n\n- alpha\n- beta\n- gamma\n");
        try
        {
            await _fx.DispatchAsync(async () =>
            {
                var docVm = new MarkdownDocumentViewModel("notes.md", docPath);
                var view = new MarkdownDocumentView { DataContext = docVm };
                var window = new Window { Width = 700, Height = 500, Content = view };
                window.RequestedThemeVariant = ThemeVariant.Dark;
                window.Show();
                await HeadlessRender.SettleAsync(window);

                var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
                    .OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
                System.IO.File.WriteAllText("/tmp/ux-md-diag.txt",
                    "MarkdownDocumentView standalone texts:\n" + string.Join(" | ", texts.Where(s => s.Length > 0)));

                await ScreenshotCapture.CaptureControlAsync(window, view, "/tmp/ux-md-standalone.png");
                window.Close();
            });
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }
}
