using System.Diagnostics;
using System.Text;
using Avalonia;
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
/// Drives a REAL kilo agent through the real cockpit (real PTY via PortaPtyLauncher, real kilo binary,
/// real MCP server in-process) to verify the terminal pane renders kilo's interactive TUI and the pane
/// does not land in an error state. This is the "drive the repl for real" check for the kilo runtime.
/// </summary>
[Collection("Avalonia")]
public class KiloRealTuiTests
{
    private readonly HeadlessAvaloniaFixture _fx;
    public KiloRealTuiTests(HeadlessAvaloniaFixture fx) => _fx = fx;

    private sealed class FakeWatcher : IFileWatcher
    {
        public Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static bool KiloInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("kilo", "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string TerminalText(Visual root)
    {
        var sb = new StringBuilder();
        foreach (var tb in root.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.Name == "ScreenText"))
        {
            if (tb.Inlines is null) continue;
            foreach (var inline in tb.Inlines) if (inline is Run r) sb.Append(r.Text);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public Task Real_kilo_overview_renders_in_the_pane_and_is_not_error()
    {
        return _fx.DispatchAsync(async () =>
        {
            if (!KiloInstalled()) return;   // kilo not on PATH — nothing to drive

            var root = Path.Combine(Path.GetTempPath(), "kilo-real-" + Guid.NewGuid().ToString("N"));
            var cfg = ProjectScaffolder.Ensure(root);
            MainWindowViewModel? vm = null;
            Window? window = null;
            try
            {
                // The real app sets these at startup (App.axaml.cs); the headless TestApp doesn't, so
                // mirror production here or the prompt's Enter is dropped by kilo's still-booting TUI.
                AgentSession.InjectSettleDelay = TimeSpan.FromMilliseconds(1500);
                AgentSession.InjectEnterRetryDelay = TimeSpan.FromMilliseconds(1000);

                vm = await MainWindowViewModel.InitializeAsync(
                    cfg.ChannelRoot, new PortaPtyLauncher(), new FakeWatcher(),
                    repoRoot: cfg.Root, overviewSystemPromptPath: cfg.SystemPromptPath,
                    defaultAgentRuntime: AgentRuntimeKind.Kilo);

                var pane = Assert.Single(vm.Panes);
                Assert.Equal(AgentRuntimeKind.Kilo, pane.Runtime);

                window = new MainWindow { DataContext = vm, Width = 1200, Height = 1600 };
                window.Show();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                // Poll until the real kilo TUI produces renderable output. Task.Delay is not real-time
                // inside Avalonia's headless dispatch, so wait on a real thread-pool sleep to give the
                // spawned kilo process actual wall-clock time to paint its TUI.
                var deadline = DateTime.UtcNow.AddSeconds(120);
                string text = "";
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Run(() => Thread.Sleep(2000));
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    text = TerminalText(window);
                    // Break on real (non-blank) TUI content — kilo's initial frame is a blank box.
                    if (text.Any(c => !char.IsWhiteSpace(c))) break;
                }

                // The terminal must render kilo's TUI: any non-whitespace glyph proves the VT pipeline
                // delivered kilo's frames to the pane, and the interactive session stays alive (not a
                // headless one-shot that exited).
                Assert.True(text.Any(c => !char.IsWhiteSpace(c)),
                    $"kilo TUI produced no renderable content within 120s — pane='{pane.HookStateText}' " +
                    $"pty={(pane.CurrentPty is null ? "null" : "attached")}");

                // Regression guard for "TUI renders only in the top half": kilo (opencode) reads its size
                // from the PTY winsize, so it must paint content down to the BOTTOM of the terminal — not
                // just the top ~24 rows. Assert the last non-blank rendered row is in the bottom quarter.
                var terminal = Assert.Single(window.GetVisualDescendants().OfType<TerminalControl>());
                var rows = terminal.RenderedText.Split('\n');
                var lastNonBlank = 0;
                for (int i = 0; i < rows.Length; i++) if (!string.IsNullOrWhiteSpace(rows[i])) lastNonBlank = i;
                var ptyRows = terminal.PtyRows;
                Assert.True(lastNonBlank >= ptyRows * 0.75,
                    $"kilo TUI stops at row {lastNonBlank}/{ptyRows} — painted for a stale (smaller) terminal " +
                    "and never repainted after the PTY resize (top-half bug)");
                // The interactive TUI stays alive (a headless 'kilo run' would have exited) — the pane
                // must not be stuck in the exited state while the agent is live.
                Assert.NotEqual("exited", pane.HookStateText);
                Assert.False(pane.NeedsYou);
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
