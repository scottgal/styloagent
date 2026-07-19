using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mostlylucid.Avalonia.UITesting;
using Mostlylucid.Avalonia.UITesting.Players;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Git;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.UITests;

[Collection("Avalonia")]
public class AgentRuntimeButtonsViewTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public AgentRuntimeButtonsViewTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class RecordingLauncher : IPtyLauncher
    {
        public List<PtySpawnOptions> Options { get; } = new();

        public Task<IPtySession> SpawnAsync(PtySpawnOptions o, CancellationToken ct = default)
        {
            Options.Add(o);
            return Task.FromResult<IPtySession>(new FakePtySession());
        }
    }

    private sealed class NoWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string p, TimeSpan t, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class OneWorktree : IGitReader
    {
        private readonly string _dir;
        public OneWorktree(string dir) => _dir = dir;
        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string root, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GitWorktree>>(new[] { new GitWorktree(_dir, "test", "abc") });
    }

    [Fact]
    public async Task NewClaude_and_NewCodex_buttons_render_and_NewCodex_spawns_codex()
    {
        const string buttonsPath = "/tmp/styloagent-runtime-buttons.png";
        const string codexPath = "/tmp/styloagent-codex-pane.png";
        if (System.IO.File.Exists(buttonsPath)) System.IO.File.Delete(buttonsPath);
        if (System.IO.File.Exists(codexPath)) System.IO.File.Delete(codexPath);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sty-runtime-buttons-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);

        try
        {
            await _fx.DispatchAsync(async () =>
            {
                var launcher = new RecordingLauncher();
                var vm = await MainWindowViewModel.InitializeAsync(
                    "/tmp/no-channel", launcher, new NoWatcher(), new OneWorktree(dir), dir);

                var window = new MainWindow { DataContext = vm, Width = 1100, Height = 650 };
                window.DataTemplates.Add(new FuncDataTemplate<AgentPaneViewModel>((_, _) => new AgentPaneView(), true));
                window.DataTemplates.Add(new FuncDataTemplate<BusViewModel>((_, _) => new BusView(), true));
                window.Show();
                await HeadlessRender.SettleAsync(window);

                var newClaude = window.FindControl<Button>("NewClaudeButton");
                var newCodex = window.FindControl<Button>("NewCodexButton");
                Assert.NotNull(newClaude);
                Assert.NotNull(newCodex);
                Assert.True(newClaude!.Bounds.Width > 90);
                Assert.True(newCodex!.Bounds.Width > 90);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "New Claude");
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "New Codex");

                await ScreenshotCapture.CaptureWindowAsync(window, buttonsPath, settle: true);

                await using var session = await UITestSession.AttachAsync(window);
                await session.ClickAsync("name=NewCodexButton");
                await WaitUntilAsync(async () =>
                {
                    await HeadlessRender.SettleAsync(window);
                    return launcher.Options.Count >= 2;
                });

                Assert.Equal(2, vm.Panes.Count);
                Assert.Equal(AgentRuntimeKind.Codex, vm.Panes[1].Runtime);
                Assert.Equal("codex", launcher.Options[1].Command);
                Assert.Contains("--config", launcher.Options[1].Args);
                Assert.Contains(launcher.Options[1].Args, a => a.Contains("hooks.SessionStart", StringComparison.Ordinal));
                Assert.DoesNotContain(launcher.Options[1].Args, a => a is "--settings" or "--mcp-config");

                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await ScreenshotCapture.CaptureWindowAsync(window, codexPath, settle: true);

                window.Close();
                vm.Dispose();
            });

            Assert.True(System.IO.File.Exists(buttonsPath), "runtime button screenshot should be written");
            Assert.True(System.IO.File.Exists(codexPath), "codex pane screenshot should be written");
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 25)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
    }
}
