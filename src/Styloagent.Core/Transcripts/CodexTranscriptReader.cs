using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Styloagent.Core.Transcripts;

/// <summary>
/// Reads the current context-window usage from a Codex CLI session transcript. Codex writes its
/// session log beneath <c>~/.codex/sessions</c>; a <c>token_count</c> event supplies both the
/// current context input-token count and the model-selected context-window limit.
/// </summary>
public static class CodexTranscriptReader
{
    private static readonly ConcurrentDictionary<string, string> PathsBySessionId = new(StringComparer.Ordinal);

    /// <summary>Returns Codex's current context usage for <paramref name="sessionId"/>, or null when unavailable.</summary>
    public static TranscriptUsage? ReadLatestForSession(string? sessionId)
    {
        var path = PathForSession(sessionId);
        return path is null ? null : ReadLatest(path);
    }

    /// <summary>
    /// Finds a session transcript by its hook-provided session id. Codex stores sessions in date
    /// folders, so the id suffix is the stable lookup key rather than the current working directory.
    /// </summary>
    public static string? PathForSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var id = sessionId.Trim();
        if (PathsBySessionId.TryGetValue(id, out var cached))
        {
            if (File.Exists(cached)) return cached;
            PathsBySessionId.TryRemove(id, out _);
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(root)) return null;

        var suffix = "-" + id + ".jsonl";
        try
        {
            var path = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (path is not null) PathsBySessionId.TryAdd(id, path);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Reads the newest valid Codex <c>token_count</c> event from a transcript. A partial event,
    /// missing context limit, or unreadable file is unavailable rather than an inferred limit.
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

    private static bool TryParseUsage(string line, out TranscriptUsage usage)
    {
        usage = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "type") != "event_msg"
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || Str(payload, "type") != "token_count"
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("last_token_usage", out var last)
                || last.ValueKind != JsonValueKind.Object)
                return false;

            var used = Num(last, "input_tokens");
            var window = Num(info, "model_context_window");
            if (used <= 0 || window <= 0) return false;

            usage = new TranscriptUsage(used, window, null);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Num(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n : 0;

    private static IEnumerable<string> TailLines(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        var take = (int)Math.Min(length, maxBytes);
        fs.Seek(length - take, SeekOrigin.Begin);
        var bytes = new byte[take];
        _ = fs.Read(bytes, 0, take);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n');
        var firstComplete = take < length ? 1 : 0;
        for (var i = lines.Length - 1; i >= firstComplete; i--)
        {
            var line = lines[i].Trim();
            if (line.Length > 0) yield return line;
        }
    }
}
