using System.Globalization;
using System.Text;

namespace Styloagent.Core.Channel;

/// <summary>
/// Writes a bus message to the channel as a markdown trace file — the durable ledger the whole
/// cockpit reads (<see cref="ChannelProjection"/>, the bus panel, the timeline). This is the write
/// half of the file-drop protocol, now driven by the <c>send_message</c> MCP tool instead of the
/// agent hand-writing files: the app owns the format, so the trace is always well-formed and routing
/// is exact. A message lands in <c>inbox/&lt;to&gt;&lt;slug&gt;.md</c>; the routing prefix (<paramref /> "to")
/// is what <see cref="MessageRouting"/> resolves to a recipient.
/// </summary>
public static class ChannelMessageWriter
{
    /// <summary>
    /// Writes the message and returns the file path. <paramref name="to"/> is a recipient routing
    /// prefix (e.g. <c>router-</c>, or <c>all-</c> to broadcast); a missing trailing '-' is added.
    /// The slug is derived from <paramref name="subject"/> and de-duplicated so a repeated subject
    /// never overwrites an earlier message.
    /// </summary>
    public static string Write(
        string channelRoot, string from, string to, string subject, string body,
        string priority, DateTimeOffset timestamp, string? fromRepo = null)
    {
        var inbox = Path.Combine(channelRoot, "inbox");
        Directory.CreateDirectory(inbox);

        var routingPrefix = NormalizeRecipient(to);
        var baseSlug = Slug(subject);

        var id = routingPrefix + baseSlug;
        var n = 1;
        while (File.Exists(Path.Combine(inbox, id + ".md")))
            id = $"{routingPrefix}{baseSlug}-{++n}";

        var path = Path.Combine(inbox, id + ".md");
        File.WriteAllText(path, Format(from, subject, body, priority, timestamp, fromRepo));
        return path;
    }

    /// <summary>
    /// Writes the immutable completion report for an existing bus thread. A reply is deliberately a
    /// separate outbox record rather than an edit of the inbound message; the projection recognizes it
    /// and moves the whole thread from the live queue into Archive.
    /// </summary>
    public static string Reply(
        string channelRoot, string from, string thread, string body, DateTimeOffset timestamp)
    {
        var outbox = Path.Combine(channelRoot, "outbox");
        Directory.CreateDirectory(outbox);
        var slug = Slug(thread);
        var path = Path.Combine(outbox, slug + ".reply.md");
        if (File.Exists(path))
            throw new InvalidOperationException($"thread '{slug}' already has an immutable completion report");

        File.WriteAllText(path, Format(from, thread, body, "normal", timestamp));
        return path;
    }

    // Bug A: <paramref name="fromRepo"/> stamps an optional **From-Repo:** header so a cross-repo reply can
    // route home; blank/null omits the header entirely (single-repo back-compat — the trace is byte-identical
    // to the pre-Bug-A format).
    internal static string Format(
        string from, string subject, string body, string priority, DateTimeOffset timestamp, string? fromRepo = null)
    {
        var fromRepoLine = string.IsNullOrWhiteSpace(fromRepo)
            ? ""
            : $"**From-Repo:** {fromRepo.Trim()}\n";
        return
            $"**From:** {(string.IsNullOrWhiteSpace(from) ? "unknown" : from.Trim())}\n" +
            fromRepoLine +
            $"**Timestamp:** {timestamp.ToString("o", CultureInfo.InvariantCulture)}\n" +
            $"**Priority:** {NormalizePriority(priority)}\n\n" +
            $"# {subject.Trim()}\n\n{body.Trim()}\n";
    }

    /// <summary>Ensures a recipient reads as a routing prefix: lowercased, trimmed, trailing '-'.</summary>
    public static string NormalizeRecipient(string to)
    {
        var t = (to ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return "all-";
        return t.EndsWith('-') ? t : t + "-";
    }

    /// <summary>Coerces free-text priority to what <see cref="ChannelProjection"/> understands.</summary>
    internal static string NormalizePriority(string priority) =>
        (priority?.Trim().ToLowerInvariant()) switch
        {
            "urgent" or "high"        => "urgent",
            "low"                     => "low",
            "info" or "informational" => "info",
            _                         => "normal",
        };

    private static string Slug(string subject)
    {
        var sb = new StringBuilder();
        foreach (var c in (subject ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if ((c == ' ' || c == '-' || c == '_') && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return slug.Length == 0 ? "message" : slug;
    }
}
