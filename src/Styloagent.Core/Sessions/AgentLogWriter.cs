using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Styloagent.Core.Hooks;
using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Slice 1 of the agent-log design (2026-07-17): the per-agent log WRITER. On each turn boundary it projects
/// the newly-completed transcript turn(s) into a timestamped markdown block and APPENDS to
/// <c>&lt;logsDir&gt;/&lt;prefix&gt;.md</c>. The log is a derived projection — the JSONL transcript stays the
/// source of truth — so writing is:
/// <list type="bullet">
///   <item><b>Keyed by prefix</b>, not session id, so one file spans the agent's whole life (survives
///     dehydrate → rehydrate → re-spawn).</item>
///   <item><b>Incremental + idempotent</b>: a per-agent cursor (persisted alongside the log) records which
///     transcript lines were already projected, so re-projecting is a no-op and the file only grows.</item>
///   <item><b>Re-spawn aware</b>: a new session id for the same prefix appends after a lifecycle separator,
///     never overwriting the earlier history.</item>
///   <item><b>Best-effort</b> (degrade, never destroy): an unreadable/garbled transcript or a disk error is
///     traced and skipped — it never throws into or stalls the agent, and never writes a corrupt file.</item>
/// </list>
/// v1 logs the message text of assistant/user turns only; tool-call turns (no text) are skipped (YAGNI).
/// </summary>
public sealed class AgentLogWriter
{
    private readonly string _logsDir;

    public AgentLogWriter(string logsDirectory)
        => _logsDir = logsDirectory ?? throw new ArgumentNullException(nameof(logsDirectory));

    /// <summary>
    /// The runtime logs directory for a project — the sidecar <c>&lt;root&gt;/.styloagent/logs</c> (gitignored,
    /// alongside <c>channel/</c> and <c>issues/</c>). Owns the path convention so a host only needs the root.
    /// </summary>
    public static string LogsDirFor(string projectRoot) => Path.Combine(projectRoot, ".styloagent", "logs");

    /// <summary>The per-agent log file path for <paramref name="prefix"/>.</summary>
    public string LogPathFor(string prefix) => Path.Combine(_logsDir, prefix + ".md");

    /// <summary>
    /// Turn-boundary entrypoint for the hook pipeline. Only <c>Stop</c> (which Claude Code fires on every
    /// completed turn) triggers a projection; every other event is ignored. Resolves the agent's transcript
    /// from the event's cwd + session id and delegates to <see cref="AppendNewTurns"/>.
    /// </summary>
    public void OnHookEvent(HookEvent e)
    {
        if (e is null || e.EventName != "Stop") return;
        var transcriptPath = TranscriptReader.PathFor(e.Cwd, e.SessionId);
        AppendNewTurns(e.AgentId, e.SessionId, transcriptPath);
    }

    /// <summary>
    /// Projects any not-yet-logged turns of <paramref name="transcriptPath"/> into
    /// <c>&lt;prefix&gt;.md</c>. Best-effort: any failure is traced and swallowed. <paramref name="sessionId"/>
    /// drives re-spawn detection (a change of session for the same prefix inserts a lifecycle separator).
    /// </summary>
    public void AppendNewTurns(string prefix, string? sessionId, string? transcriptPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prefix)) return;
            if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return;

            var lines = ReadLinesShared(transcriptPath);

            var cursor = LoadCursor(prefix);
            bool sameSession = cursor.SessionId is not null && cursor.SessionId == sessionId;
            bool respawn = cursor.SessionId is not null && cursor.SessionId != sessionId;
            int start = sameSession ? cursor.Count : 0;

            var blocks = new List<(string Heading, string Text)>();
            for (int i = start; i < lines.Count; i++)
                if (TryParseTurn(lines[i], out var role, out var text, out var stamp))
                    blocks.Add(($"## {stamp} · {role}", text));

            var logPath = LogPathFor(prefix);
            bool fileHasContent = File.Exists(logPath) && new FileInfo(logPath).Length > 0;

            if (blocks.Count > 0)
            {
                Directory.CreateDirectory(_logsDir);
                var sb = new StringBuilder();
                if (!fileHasContent) sb.Append("# Agent log — ").Append(prefix).Append("\n\n");
                // A re-spawn for the same prefix is separated from earlier history — but never overwrites it.
                if (respawn && fileHasContent)
                    sb.Append("---\n<!-- re-spawn ").Append(FirstStamp(blocks)).Append(" -->\n\n");
                foreach (var (heading, text) in blocks)
                    sb.Append(heading).Append('\n').Append(text).Append("\n\n");
                File.AppendAllText(logPath, sb.ToString());
            }

            // Advance the cursor past everything we've now seen. We commit the session switch once the new
            // session has produced a block (or on the very first sighting) so a re-spawn separator is never
            // dropped between a bare-session sighting and its first real turn.
            if (blocks.Count > 0 || sameSession || cursor.SessionId is null)
                SaveCursor(prefix, sessionId, lines.Count);
        }
        catch (Exception ex)
        {
            // Degrade, never destroy: the transcript remains the source of truth; a projection failure is
            // logged for diagnostics and otherwise ignored.
            Trace.WriteLine($"[AgentLogWriter] projection failed for '{prefix}': {ex}");
        }
    }

    // ── turn parsing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses one transcript JSONL line into a projectable turn. Yields false for lines that carry no
    /// assistant/user message text — tool-only turns (tool_use / tool_result) and non-message lines — so v1
    /// logs conversation text only. The heading timestamp is the line's ISO <c>timestamp</c>, normalised to
    /// UTC; an unparseable/absent one falls back to the raw value (or empty).
    /// </summary>
    private static bool TryParseTurn(string line, out string role, out string text, out string stamp)
    {
        role = ""; text = ""; stamp = "";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return false;

            var r = Str(msg, "role") ?? Str(root, "type");
            if (r != "assistant" && r != "user") return false;

            var sb = new StringBuilder();
            if (msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    sb.Append(content.GetString());
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (block.ValueKind == JsonValueKind.Object
                            && Str(block, "type") == "text" && Str(block, "text") is { } bx)
                            sb.Append(bx);
            }

            var body = sb.ToString().Trim();
            if (body.Length == 0) return false;   // tool-only or empty turn — skip

            role = r;
            text = body;
            stamp = FormatStamp(Str(root, "timestamp"));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FormatStamp(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : iso;   // keep whatever the transcript had rather than lose the marker
    }

    private static string FirstStamp(List<(string Heading, string Text)> blocks)
    {
        // The heading is "## <stamp> · <role>" — recover the stamp for the re-spawn marker.
        var h = blocks[0].Heading;
        const string dot = " · ";
        int cut = h.IndexOf(dot, StringComparison.Ordinal);
        return cut > 3 ? h.Substring(3, cut - 3) : "";
    }

    /// <summary>Reads all non-empty lines, tolerating a concurrent writer (the live agent) via a shared handle.</summary>
    private static List<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = new List<string>();
        string? l;
        while ((l = reader.ReadLine()) is not null)
            if (l.Trim().Length > 0) lines.Add(l);
        return lines;
    }

    // ── cursor persistence ─────────────────────────────────────────────────
    // A small sidecar per prefix under a hidden .cursors/ subdir keeps re-projection idempotent ACROSS
    // process restarts (a cockpit rebuild+restart is routine here), without polluting the markdown that the
    // doc search indexes. Everything here is best-effort: a missing/corrupt cursor just re-reads from 0.

    private readonly record struct Cursor(string? SessionId, int Count);

    private string CursorPathFor(string prefix) => Path.Combine(_logsDir, ".cursors", prefix + ".json");

    private Cursor LoadCursor(string prefix)
    {
        try
        {
            var p = CursorPathFor(prefix);
            if (!File.Exists(p)) return new Cursor(null, 0);
            return JsonSerializer.Deserialize<Cursor>(File.ReadAllText(p));
        }
        catch { return new Cursor(null, 0); }
    }

    private void SaveCursor(string prefix, string? sessionId, int count)
    {
        try
        {
            var p = CursorPathFor(prefix);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(new Cursor(sessionId, count)));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AgentLogWriter] cursor save failed for '{prefix}': {ex}");
        }
    }
}
