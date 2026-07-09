using System.Text;
using Styloagent.Core.Sessions;
using Styloagent.Terminal;
using Xunit;

/// <summary>
/// REAL end-to-end: launches the actual <c>claude</c> binary through the full PTY stack,
/// with an EMPTY working directory — exactly reproducing the app's seed (Worktree = "").
/// This is the test the fake-PTY unit tests could never be: it proves that (1) an empty
/// worktree no longer throws <c>ArgumentNullException</c> and (2) real claude launches and
/// streams output through our <see cref="PortaPtyLauncher"/>/<see cref="PortaPtySession"/>.
/// Requires <c>claude</c> on PATH; tagged Integration so environments without it can skip.
/// </summary>
public class ClaudeLaunchIntegrationTests
{
    [Fact(Timeout = 20000)]
    [Trait("Category", "Integration")]
    public async Task Launches_real_claude_with_empty_worktree()
    {
        var launcher = new PortaPtyLauncher();
        var buf = new StringBuilder();
        var got = new TaskCompletionSource<string>();

        // Empty cwd on purpose — the bug the app hit. The resolver must make this launch.
        await using var pty = await launcher.SpawnAsync(
            new PtySpawnOptions("claude", new[] { "--version" }, "", null, 100, 30));

        pty.Output += chunk =>
        {
            lock (buf)
            {
                buf.Append(chunk);
                if (buf.ToString().Contains("Claude Code"))
                    got.TrySetResult(buf.ToString());
            }
        };

        var completed = await Task.WhenAny(got.Task, Task.Delay(15000));
        string captured;
        lock (buf) { captured = buf.ToString(); }
        Assert.True(completed == got.Task,
            $"Expected 'Claude Code' in `claude --version` output launched via the PTY stack; got: <{captured}>");
    }
}
