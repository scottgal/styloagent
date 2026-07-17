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
    private string DeliveredPath(string recipientPrefix) => DeliveryHookCommands.DeliveredFile(_hooksDir, Key(recipientPrefix));

    /// <summary>
    /// Enqueue a delivery <paramref name="noteLine"/> for <paramref name="recipientPrefix"/>.
    /// <paramref name="pushing"/> selects the channel: pushing (urgent/normal) accumulates into the
    /// <c>.push</c> file the Stop hook force-continues on; surfacing (low/info) accumulates into the
    /// <c>.info</c> file the UserPromptSubmit hook shows without forcing a turn. When
    /// <paramref name="deliveredPath"/> is supplied it is recorded in the durable delivered ledger, so the
    /// per-message "picked up" fact can flip true once this note drains (see <see cref="PickedUp"/>).
    /// </summary>
    public void Enqueue(string recipientPrefix, string noteLine, bool pushing, string? deliveredPath = null)
    {
        string path = pushing ? PushPath(recipientPrefix) : InfoPath(recipientPrefix);
        lock (_gate)
        {
            Directory.CreateDirectory(DeliverDir);
            string raw = ReadRaw(path);
            raw += noteLine.TrimEnd('\r', '\n') + "\n";
            WriteJsonStringAtomic(path, raw);
            if (deliveredPath is not null)
                RecordDelivered(recipientPrefix, deliveredPath);
        }
    }

    /// <summary>
    /// Record that the message at <paramref name="filePath"/> was delivered to <paramref name="recipientPrefix"/>
    /// (enqueued to its pending queue, or injected directly into an idle session) — the durable half of the
    /// "picked up" derivation. Idempotent. Use for the inject path where no note is enqueued; the
    /// <see cref="Enqueue"/> overload records it inline for the pending path.
    /// </summary>
    public void MarkDelivered(string recipientPrefix, string filePath)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DeliverDir);
            RecordDelivered(recipientPrefix, filePath);
        }
    }

    /// <summary>
    /// True once the message at <paramref name="filePath"/>, delivered to <paramref name="recipientPrefix"/>,
    /// has been <b>picked up</b>: it was recorded as delivered AND no push/info note referencing it is still
    /// pending — i.e. the recipient's turn-boundary hook (or an in-session <c>check_inbox</c>) has drained it,
    /// so the recipient has begun handling it. False while the note is still queued, or if never delivered.
    ///
    /// Derived, not stored: the hook drain is a POSIX-<c>sh</c> snippet with no C# in the loop, so pickup is
    /// observed from delivery state rather than written at drain time. A note still references its own
    /// <paramref name="filePath"/> verbatim while pending (the nudge carries the path), so its absence from the
    /// live deliver files is what marks the drain.
    /// </summary>
    public bool PickedUp(string recipientPrefix, string filePath)
    {
        lock (_gate)
            return ReadDelivered(recipientPrefix).Contains(filePath) && !IsStillPending(recipientPrefix, filePath);
    }

    /// <summary>A note is still pending iff the live push or info deliver file still references its FilePath.</summary>
    private bool IsStillPending(string recipientPrefix, string filePath) =>
        ReadRaw(PushPath(recipientPrefix)).Contains(filePath, StringComparison.Ordinal) ||
        ReadRaw(InfoPath(recipientPrefix)).Contains(filePath, StringComparison.Ordinal);

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

    /// <summary>Add <paramref name="filePath"/> to the recipient's delivered ledger if absent. Caller holds the gate.</summary>
    private void RecordDelivered(string recipientPrefix, string filePath)
    {
        var set = ReadDelivered(recipientPrefix);
        if (set.Add(filePath))
            WriteDeliveredAtomic(DeliveredPath(recipientPrefix), set);
    }

    /// <summary>The set of FilePaths delivered to <paramref name="recipientPrefix"/> (newline-delimited on disk;
    /// FilePaths never contain a newline). Missing/unreadable ledger → empty (degrade: nothing known delivered).</summary>
    private HashSet<string> ReadDelivered(string recipientPrefix)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string path = DeliveredPath(recipientPrefix);
            if (!File.Exists(path)) return set;
            foreach (string line in File.ReadAllLines(path))
                if (line.Length > 0) set.Add(line);
        }
        catch { /* unreadable ledger → treat as nothing delivered */ }
        return set;
    }

    /// <summary>Write the delivered set as newline-delimited text via a temp file + atomic rename.</summary>
    private static void WriteDeliveredAtomic(string path, IEnumerable<string> paths)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join('\n', paths));
        File.Move(tmp, path, overwrite: true);
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
