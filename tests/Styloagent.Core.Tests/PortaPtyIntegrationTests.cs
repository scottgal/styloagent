using Styloagent.Core.Sessions;
using Styloagent.Terminal;

namespace Styloagent.Core.Tests;

/// <summary>
/// Integration test: spawns a real /bin/bash process and round-trips a command through the PTY.
/// Requires macOS / Linux. Tagged Integration so it can be filtered separately.
/// </summary>
public class PortaPtyIntegrationTests
{
    private static readonly string[] NoRcArgs = ["--norc"];

    [Fact(Timeout = 15_000)]
    [Trait("Category", "Integration")]
    public async Task Spawns_bash_writes_and_reads_output()
    {
        var launcher = new PortaPtyLauncher();
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opts = new PtySpawnOptions(
            Command: "/bin/bash",
            Args: NoRcArgs,
            WorkingDirectory: Environment.CurrentDirectory,
            Env: null,
            Cols: 80,
            Rows: 24);

        await using var pty = await launcher.SpawnAsync(opts);
        pty.Output += chunk => { if (chunk.Contains("HELLO_PTY")) got.TrySetResult(chunk); };

        // Give bash a moment to initialise before writing
        await Task.Delay(200);
        await pty.WriteAsync("echo HELLO_PTY\n");

        var completed = await Task.WhenAny(got.Task, Task.Delay(10_000));
        Assert.Same(got.Task, completed);
    }
}
