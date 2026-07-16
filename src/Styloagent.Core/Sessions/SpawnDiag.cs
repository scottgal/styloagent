using System.Text;

namespace Styloagent.Core.Sessions;

/// <summary>
/// TEMP diagnostic logger for the "spawned agent exits ~4s after launch" blocker
/// (issue blocker-mcp-spawn-agent-produces-an-exited-membe). Appends timestamped lines to
/// <c>/tmp/styloagent-spawn-debug.log</c> so a single restart + spawn reveals WHO tears the PTY down
/// (an explicit Kill via DisposeAsync — with its call stack — vs. a genuine process exit). Remove once
/// the root cause is confirmed and fixed.
/// </summary>
public static class SpawnDiag
{
    private static readonly object Gate = new();
    private static readonly string Path =
        Environment.GetEnvironmentVariable("STYLOAGENT_SPAWN_DEBUG_LOG") ?? "/tmp/styloagent-spawn-debug.log";

    public static void Log(string message, bool includeStack = false)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
              .Append(" [tid=").Append(Environment.CurrentManagedThreadId).Append("] ")
              .Append(message);
            if (includeStack)
                sb.Append('\n').Append(new System.Diagnostics.StackTrace(1, fNeedFileInfo: false));
            sb.Append('\n');
            lock (Gate) File.AppendAllText(Path, sb.ToString());
        }
        catch { /* diagnostics must never affect behaviour */ }
    }
}
