using System.Text;
using System.Text.Json;
using Styloagent.Core.Hooks;

namespace Styloagent.Core.Channel;

/// <summary>
/// The MCP-native delivery queue: a durable, file-backed, per-recipient store of *surfaced-pending*
/// delivery notes (design: <c>docs/superpowers/specs/2026-07-13-mcp-native-delivery-design.md</c>).
///
/// It is NOT the channel — it is disposable, rebuildable delivery-state under the per-run hooks dir, so
/// degrade-never-destroy holds: losing it never loses a message (the durable channel still has it; worst
/// case a note is re-surfaced — at-least-once, never at-most-once). The recipient's own turn-boundary
/// hook drains it (see <see cref="DeliveryHookCommands"/>); the <c>check_inbox</c> MCP verb drains the
/// same store in-process. Both share the file layout, so writer and hook always agree.
///
/// Keyed by <see cref="HookSettings.SanitizeAgentId"/> of the recipient prefix — the same token the hook
/// commands use in the normal (unique-prefix) case, consistent with the rest of delivery (routing and the
/// injector already key by prefix). Each file holds a single JSON string of accumulated raw text, so a
/// POSIX-<c>sh</c> hook embeds it raw with no escaping.
/// </summary>
public sealed class PendingInbox
{
    private readonly string _hooksDir;
    private readonly object _gate = new();

    public PendingInbox(string hooksDir) => _hooksDir = hooksDir;

    /// <summary>The directory holding every recipient's deliver files (created lazily on first write).</summary>
    public string DeliverDir => DeliveryHookCommands.DeliverDir(_hooksDir);

    private static string Key(string recipientPrefix) => HookSettings.SanitizeAgentId(recipientPrefix);

    private string PushPath(string recipientPrefix) => DeliveryHookCommands.PushFile(_hooksDir, Key(recipientPrefix));
    private string InfoPath(string recipientPrefix) => DeliveryHookCommands.InfoFile(_hooksDir, Key(recipientPrefix));

    /// <summary>
    /// Enqueue a delivery <paramref name="noteLine"/> for <paramref name="recipientPrefix"/>.
    /// <paramref name="pushing"/> selects the channel: pushing (urgent/normal) accumulates into the
    /// <c>.push</c> file the Stop hook force-continues on; surfacing (low/info) accumulates into the
    /// <c>.info</c> file the UserPromptSubmit hook shows without forcing a turn.
    /// </summary>
    public void Enqueue(string recipientPrefix, string noteLine, bool pushing)
    {
        string path = pushing ? PushPath(recipientPrefix) : InfoPath(recipientPrefix);
        lock (_gate)
        {
            Directory.CreateDirectory(DeliverDir);
            string raw = ReadRaw(path);
            raw += noteLine.TrimEnd('\r', '\n') + "\n";
            WriteJsonStringAtomic(path, raw);
        }
    }

    /// <summary>True if <paramref name="recipientPrefix"/> has any pending push or info note.</summary>
    public bool HasPending(string recipientPrefix)
    {
        lock (_gate)
            return NonEmpty(PushPath(recipientPrefix)) || NonEmpty(InfoPath(recipientPrefix));
    }

    /// <summary>
    /// Drain and return everything pending for <paramref name="recipientPrefix"/> (push notes first, then
    /// info), clearing the store — the in-process twin of the hook drain, used by the <c>check_inbox</c>
    /// verb. Returns an empty string when nothing is pending. Atomic-claims each file so it never
    /// double-reads content a concurrent hook is taking.
    /// </summary>
    public string DrainFormatted(string recipientPrefix)
    {
        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (string path in new[] { PushPath(recipientPrefix), InfoPath(recipientPrefix) })
            {
                string raw = Claim(path);
                if (raw.Length > 0) sb.Append(raw);
            }
        }
        return sb.ToString();
    }

    private static bool NonEmpty(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    /// <summary>Read a deliver file back to raw text (it stores a JSON string); missing/bad → empty.</summary>
    private static string ReadRaw(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            string json = File.ReadAllText(path);
            return string.IsNullOrEmpty(json) ? "" : JsonSerializer.Deserialize<string>(json) ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Atomically claim (rename-away) a deliver file and return its raw text; missing → empty.</summary>
    private static string Claim(string path)
    {
        string taken = path + ".take";
        try
        {
            if (!File.Exists(path)) return "";
            File.Move(path, taken, overwrite: true);
            string raw = ReadRaw(taken);
            File.Delete(taken);
            return raw;
        }
        catch { return ""; }
    }

    /// <summary>Write raw text as a single JSON string via a temp file + atomic rename.</summary>
    private static void WriteJsonStringAtomic(string path, string raw)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(raw));
        File.Move(tmp, path, overwrite: true);
    }
}
