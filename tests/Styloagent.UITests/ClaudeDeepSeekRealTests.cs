using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Styloagent.App.ViewModels;
using Styloagent.App.Views;
using Styloagent.Core.Abstractions;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;
using Xunit;

namespace Styloagent.UITests;

/// <summary>
/// The REAL acceptance test for the dogfood: the overview must actually START as Claude Code routed
/// through DeepSeek. Drives the real cockpit (real PortaPtyLauncher + real claude + the DeepSeek env),
/// renders the window, and asserts the pane comes up Live with claude's TUI painting the pane — the
/// same scenario that regressed to real-Anthropic Opus.
/// </summary>
[Collection("Avalonia")]
public class ClaudeDeepSeekRealTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public ClaudeDeepSeekRealTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class FakeWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static bool ClaudeInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("claude", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool DeepSeekEnvAvailable()
        => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".styloagent", "deepseek.env"));

    [Fact]
    public Task Real_claude_deepseek_overview_starts_and_renders()
    {
        return _fx.DispatchAsync(async () =>
        {
            if (!ClaudeInstalled() || !DeepSeekEnvAvailable()) return;

            var root = Path.Combine(Path.GetTempPath(), "cds-real-" + Guid.NewGuid().ToString("N"));
            var cfg = ProjectScaffolder.Ensure(root);
            MainWindowViewModel? vm = null;
            Window? window = null;
            try
            {
                AgentSession.InjectSettleDelay = TimeSpan.FromMilliseconds(1500);
                AgentSession.InjectEnterRetryDelay = TimeSpan.FromMilliseconds(1000);

                vm = await MainWindowViewModel.InitializeAsync(
                    cfg.ChannelRoot, new PortaPtyLauncher(), new FakeWatcher(),
                    repoRoot: cfg.Root, overviewSystemPromptPath: cfg.SystemPromptPath,
                    defaultAgentRuntime: AgentRuntimeKind.ClaudeDeepSeek);
                // The real app applies the Dracula theme — reproduce that to catch theme-related colour loss.
                vm.GlobalTerminalTheme = Styloagent.Terminal.TerminalTheme.Dracula;

                var pane = Assert.Single(vm.Panes);
                Assert.Equal(AgentRuntimeKind.ClaudeDeepSeek, pane.Runtime);
                // The Opus tier resolves to DeepSeek, not the claude 'opus' model.
                Assert.Contains("deepseek-v4-pro", AgentRuntimeProfile.For(AgentRuntimeKind.ClaudeDeepSeek)
                    .ModelEffortArgs(null, null, ModelTier.Opus));

                window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var deadline = DateTime.UtcNow.AddSeconds(120);
                string text = "";
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Run(() => Thread.Sleep(2000));
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    var sb = new System.Text.StringBuilder();
                    foreach (var tb in window.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.Name == "ScreenText"))
                    {
                        if (tb.Inlines is null) continue;
                        foreach (var inline in tb.Inlines) if (inline is Run r) sb.Append(r.Text);
                    }
                    text = sb.ToString();
                    if (text.Any(c => !char.IsWhiteSpace(c))) break;
                }

                Assert.True(text.Any(c => !char.IsWhiteSpace(c)),
                    $"claude-deepseek overview produced no renderable output — pane='{pane.HookStateText}'");
                Assert.NotEqual("exited", pane.HookStateText);

                // COLOR diagnostic: are the rendered runs using real colour brushes, or all default?
                var distinct = new HashSet<string>();
                var defaultFg = "FFEDEDED";
                foreach (var tb in window.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.Name == "ScreenText"))
                {
                    if (tb.Inlines is null) continue;
                    foreach (var inline in tb.Inlines)
                        if (inline is Run r && r.Foreground is Avalonia.Media.ISolidColorBrush sc)
                            distinct.Add(sc.Color.ToUInt32().ToString("X8"));
                }
                distinct.Remove(defaultFg);
                var diag = string.Join(",", distinct.Take(8));
                System.IO.File.WriteAllText("/tmp/claude-colours.txt",
                    $"distinct-fg-colours={distinct.Count} sample=[{diag}]");
                Assert.NotEmpty(distinct);   // the Claude TUI must render with colour brushes, not all-default

            }
            finally
            {
                window?.Close();
                vm?.Dispose();
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        });
    }
}
