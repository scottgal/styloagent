using System.Text;
using System.Text.RegularExpressions;
using Styloagent.Core.Hooks;
using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

/// <summary>
/// The per-agent log WRITER (slice 1 of the agent-log design, 2026-07-17). Projects completed transcript
/// turns into a timestamped markdown block appended to <c>.styloagent/logs/&lt;prefix&gt;.md</c>. Keyed by
/// prefix so it spans the agent's whole life; incremental + idempotent via a per-agent cursor; re-spawn
/// appends after a lifecycle separator (never overwrites); best-effort (a bad transcript is traced+skipped,
/// never thrown). All fixtures are synthetic JSONL — no live transcripts are touched.
/// </summary>
public class AgentLogWriterTests
{
    // ── synthetic transcript fixture ─────────────────────────────────────────
    private sealed record Turn(string Role, string Timestamp, string Text, bool ToolOnly = false);

    private static string TempLogsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "styloagent-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a synthetic Claude-Code JSONL transcript (one line per turn) and returns its path.</summary>
    private static string WriteTranscript(string dir, string name, params Turn[] turns)
    {
        var path = Path.Combine(dir, name);
        var sb = new StringBuilder();
        foreach (var t in turns) sb.Append(LineFor(t)).Append('\n');
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>One JSONL transcript line for a turn — built via the serializer so the JSON is always valid.</summary>
    private static string LineFor(Turn t)
    {
        // A tool-only turn carries no text block (assistant tool_use / user tool_result) — v1 must skip it.
        object[] content = t.ToolOnly
            ? (t.Role == "assistant"
                ? new object[] { new { type = "tool_use", name = "Bash", id = "x", input = new { } } }
                : new object[] { new { type = "tool_result", tool_use_id = "x", content = "ok" } })
            : new object[] { new { type = "text", text = t.Text } };

        var line = new
        {
            type = t.Role,
            timestamp = t.Timestamp,
            message = new { role = t.Role, content },
        };
        return System.Text.Json.JsonSerializer.Serialize(line);
    }

    private static int CountTurnHeadings(string log) => Regex.Matches(log, @"(?m)^## \d").Count;

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void AppendNewTurns_ProjectsAllTurns_AsTimestampedBlocks_InOrder()
    {
        var dir = TempLogsDir();
        try
        {
            var tx = WriteTranscript(dir, "sess1.jsonl",
                new Turn("assistant", "2026-07-17T01:47:30.000Z", "First reply"),
                new Turn("user", "2026-07-17T01:48:12.000Z", "user says hi"),
                new Turn("assistant", "2026-07-17T01:49:00.000Z", "Second reply"));

            new AgentLogWriter(dir).AppendNewTurns("session-", "sess1", tx);

            var log = File.ReadAllText(Path.Combine(dir, "session-.md"));
            Assert.StartsWith("# Agent log — session-", log);
            Assert.Equal(3, CountTurnHeadings(log));
            Assert.Contains("## 2026-07-17 01:47:30 · assistant", log);
            Assert.Contains("First reply", log);
            Assert.Contains("## 2026-07-17 01:48:12 · user", log);
            Assert.Contains("user says hi", log);
            // Order preserved.
            Assert.True(log.IndexOf("First reply", StringComparison.Ordinal)
                        < log.IndexOf("user says hi", StringComparison.Ordinal));
            Assert.True(log.IndexOf("user says hi", StringComparison.Ordinal)
                        < log.IndexOf("Second reply", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppendNewTurns_SecondPass_AppendsOnlyNewTurns_EvenWithAFreshWriter()
    {
        var dir = TempLogsDir();
        try
        {
            var tx = WriteTranscript(dir, "sess1.jsonl",
                new Turn("assistant", "2026-07-17T01:47:30.000Z", "First reply"),
                new Turn("user", "2026-07-17T01:48:12.000Z", "user says hi"));
            new AgentLogWriter(dir).AppendNewTurns("session-", "sess1", tx);

            // The agent takes another turn: the transcript grows by one line.
            File.AppendAllText(tx, LineFor(
                new Turn("assistant", "2026-07-17T01:49:00.000Z", "Third reply")) + "\n");

            // A FRESH writer (simulates a cockpit restart) must resume from the persisted cursor,
            // not re-append the first two turns.
            new AgentLogWriter(dir).AppendNewTurns("session-", "sess1", tx);

            var log = File.ReadAllText(Path.Combine(dir, "session-.md"));
            Assert.Equal(3, CountTurnHeadings(log));                       // no duplication
            Assert.Single(Regex.Matches(log, Regex.Escape("First reply")));
            Assert.Contains("Third reply", log);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppendNewTurns_ReSpawn_AppendsAfterSeparator_WithoutOverwriting()
    {
        var dir = TempLogsDir();
        try
        {
            var writer = new AgentLogWriter(dir);

            var s1 = WriteTranscript(dir, "sess1.jsonl",
                new Turn("assistant", "2026-07-17T01:47:30.000Z", "Before restart"));
            writer.AppendNewTurns("session-", "sess1", s1);

            // Re-spawn: a NEW session/transcript for the SAME prefix.
            var s2 = WriteTranscript(dir, "sess2.jsonl",
                new Turn("assistant", "2026-07-17T02:10:20.000Z", "After respawn"));
            writer.AppendNewTurns("session-", "sess2", s2);

            var log = File.ReadAllText(Path.Combine(dir, "session-.md"));
            Assert.Contains("Before restart", log);                       // not overwritten
            Assert.Contains("After respawn", log);
            Assert.Contains("<!-- re-spawn", log);                        // lifecycle separator
            Assert.Matches(@"(?m)^---\s*$", log);
            Assert.Equal(2, CountTurnHeadings(log));
            // The separator sits between the two lifecycle segments.
            Assert.True(log.IndexOf("Before restart", StringComparison.Ordinal)
                        < log.IndexOf("<!-- re-spawn", StringComparison.Ordinal));
            Assert.True(log.IndexOf("<!-- re-spawn", StringComparison.Ordinal)
                        < log.IndexOf("After respawn", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppendNewTurns_ToolOnlyTurns_AreSkipped()
    {
        var dir = TempLogsDir();
        try
        {
            var tx = WriteTranscript(dir, "sess1.jsonl",
                new Turn("assistant", "2026-07-17T01:47:30.000Z", "Real text"),
                new Turn("assistant", "2026-07-17T01:47:31.000Z", "", ToolOnly: true),
                new Turn("user", "2026-07-17T01:47:32.000Z", "", ToolOnly: true));

            new AgentLogWriter(dir).AppendNewTurns("session-", "sess1", tx);

            var log = File.ReadAllText(Path.Combine(dir, "session-.md"));
            Assert.Equal(1, CountTurnHeadings(log));                      // only the text turn
            Assert.Contains("Real text", log);
            Assert.DoesNotContain("tool_use", log);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AppendNewTurns_UnreadableTranscript_DoesNotThrow_AndWritesNoCorruptFile()
    {
        var dir = TempLogsDir();
        try
        {
            var writer = new AgentLogWriter(dir);

            // Missing file: best-effort no-op, no throw, no file.
            var ex1 = Record.Exception(() =>
                writer.AppendNewTurns("session-", "sessX", Path.Combine(dir, "does-not-exist.jsonl")));
            Assert.Null(ex1);
            Assert.False(File.Exists(Path.Combine(dir, "session-.md")));

            // Garbage (non-JSON) transcript: traced+skipped, no throw, no turn blocks.
            var garbage = Path.Combine(dir, "garbage.jsonl");
            File.WriteAllText(garbage, "not json at all\n{broken\n");
            var ex2 = Record.Exception(() => writer.AppendNewTurns("session-", "sessX", garbage));
            Assert.Null(ex2);
            if (File.Exists(Path.Combine(dir, "session-.md")))
                Assert.Equal(0, CountTurnHeadings(File.ReadAllText(Path.Combine(dir, "session-.md"))));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LogsDirFor_IsTheGitignoredStyloagentSidecar()
    {
        var root = Path.Combine("/tmp", "proj");
        Assert.Equal(Path.Combine(root, ".styloagent", "logs"), AgentLogWriter.LogsDirFor(root));
    }

    [Fact]
    public void OnHookEvent_NonStopEvent_IsANoOp()
    {
        var dir = TempLogsDir();
        try
        {
            var writer = new AgentLogWriter(dir);
            var e = new HookEvent(
                AgentId: "session-", EventName: "PreToolUse", NotificationType: null, Message: null,
                SessionId: "sess1", Cwd: "/repo");

            var ex = Record.Exception(() => writer.OnHookEvent(e));
            Assert.Null(ex);
            Assert.Empty(Directory.GetFiles(dir, "session-.md"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
