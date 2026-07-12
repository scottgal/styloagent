using System.Text;
using System.Text.Json;

namespace Styloagent.Core.Transcripts;

/// <summary>Token usage read from an agent's Claude Code transcript: current context fill + model.</summary>
public sealed record TranscriptUsage(long ContextTokens, long WindowTokens, string? Model)
{
    /// <summary>Context-window fill as a fraction 0..1 (0 when the window is unknown).</summary>
    public double ContextFraction => WindowTokens > 0 ? Math.Min(1.0, (double)ContextTokens / WindowTokens) : 0;
}

/// <summary>
/// Locates and tails a Claude Code session transcript to read the latest token usage. Claude stores
/// transcripts at <c>~/.claude/projects/&lt;escaped-cwd&gt;/&lt;session-id&gt;.jsonl</c>, where the cwd is
/// escaped by replacing every non-alphanumeric char with '-'. Each assistant line carries
/// <c>message.usage</c> (input/output/cache tokens) and <c>message.model</c>. Tolerant: any read/parse
/// failure yields null rather than throwing into the UI.
/// </summary>
public static class TranscriptReader
{
    /// <summary>The transcript file path for an agent, or null if cwd/session are missing.</summary>
    public static string? PathFor(string? cwd, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(sessionId)) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "projects", EscapeCwd(cwd), sessionId + ".jsonl");
    }

    /// <summary>Claude's project-dir encoding: every non-alphanumeric char becomes '-'.</summary>
    public static string EscapeCwd(string cwd)
    {
        var sb = new StringBuilder(cwd.Length);
        foreach (var c in cwd) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString();
    }

    /// <summary>
    /// Reads the most recent usage from the transcript at <paramref name="path"/> (scanning the tail
    /// backwards for the last assistant message with a usage block). Null if unavailable.
    /// </summary>
    public static TranscriptUsage? ReadLatest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            foreach (var line in TailLines(path, maxBytes: 256 * 1024))
                if (TryParseUsage(line, out var usage))
                    return usage;
            return null;
        }
        catch { return null; }
    }

    /// <summary>Reads up to <paramref name="maxBytes"/> from the end of the file, newest line first.</summary>
    private static IEnumerable<string> TailLines(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = fs.Length;
        var take = (int)Math.Min(len, maxBytes);
        fs.Seek(len - take, SeekOrigin.Begin);
        var buf = new byte[take];
        _ = fs.Read(buf, 0, take);
        var text = Encoding.UTF8.GetString(buf);
        var lines = text.Split('\n');
        // Skip a possibly-truncated first line when we didn't start at the file's beginning.
        var start = (take < len) ? 1 : 0;
        for (int i = lines.Length - 1; i >= start; i--)
        {
            var l = lines[i].Trim();
            if (l.Length > 0) yield return l;
        }
    }

    private static bool TryParseUsage(string line, out TranscriptUsage usage)
    {
        usage = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return false;
            if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;

            long ctx = Num(u, "input_tokens")
                     + Num(u, "cache_read_input_tokens")
                     + Num(u, "cache_creation_input_tokens");
            if (ctx <= 0) return false;

            var model = msg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;

            usage = new TranscriptUsage(ctx, WindowFor(model, ctx), model);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static long Num(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>
    /// Context window in tokens. The transcript's model id does NOT reliably encode the 1M-context
    /// variant (it reads e.g. "claude-opus-4-8" even on a 1M session), so we also infer from size: a
    /// context already past 200k can only be a 1M window.
    /// </summary>
    private static long WindowFor(string? model, long contextTokens)
    {
        if (model is not null && model.Contains("1m", StringComparison.OrdinalIgnoreCase)) return 1_000_000;
        return contextTokens > 200_000 ? 1_000_000 : 200_000;
    }
}
