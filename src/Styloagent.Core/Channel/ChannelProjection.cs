using System.Globalization;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Channel;

public sealed class ChannelProjection
{
    private static readonly Regex FromPattern =
        new(@"^\*\*From:\*\*\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TimestampPattern =
        new(@"^\*\*Timestamp:\*\*\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public async Task<IReadOnlyList<BusThread>> ReadAsync(
        string channelRoot,
        IReadOnlyCollection<string> knownPrefixes,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(channelRoot))
            return Array.Empty<BusThread>();

        var allMessages = new List<BusMessage>();

        // Enumerate all four locations
        var locations = new[]
        {
            (Path: Path.Combine(channelRoot, "inbox"), IsArchive: false, IsOutbox: false),
            (Path: Path.Combine(channelRoot, "outbox"), IsArchive: false, IsOutbox: true),
            (Path: Path.Combine(channelRoot, "archive", "inbox"), IsArchive: true, IsOutbox: false),
            (Path: Path.Combine(channelRoot, "archive", "outbox"), IsArchive: true, IsOutbox: true),
        };

        foreach (var (dir, isArchive, isOutbox) in locations)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(dir, "*.md"))
            {
                ct.ThrowIfCancellationRequested();
                var msg = await ParseMessageAsync(filePath, isArchive, knownPrefixes, ct);
                if (msg is not null)
                    allMessages.Add(msg);
            }
        }

        // Determine Replied state: inbox messages whose slug has a reply anywhere
        var replySlugs = allMessages
            .Where(m => m.Kind is BusMessageKind.Reply or BusMessageKind.BroadcastReply)
            .Select(m => m.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        allMessages = allMessages
            .Select(m =>
                m.Kind == BusMessageKind.Inbox && m.State == BusMessageState.New && replySlugs.Contains(m.Slug)
                    ? m with { State = BusMessageState.Replied }
                    : m)
            .ToList();

        // Group by slug into threads.
        // NOTE: threads are keyed on SLUG alone (cross-prefix), which assumes slugs are
        // unique per topic across all routing prefixes.  This is intentional — the
        // file-drop protocol guarantees slug uniqueness per topic so that inbox, outbox,
        // and reply files for the same conversation always collapse into one thread.
        var threads = allMessages
            .GroupBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var messages = g
                    .OrderBy(m => m.Timestamp ?? DateTimeOffset.MaxValue)
                    .ThenBy(m => m.Kind == BusMessageKind.Inbox ? 0 : 1)
                    .ToList();

                var prefixes = messages
                    .Select(m => m.RoutingPrefix)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new BusThread(g.Key, messages, prefixes);
            })
            .OrderByDescending(t =>
                t.Messages
                    .Select(m => m.Timestamp)
                    .Where(ts => ts.HasValue)
                    .Select(ts => ts!.Value)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max())
            .ToList();

        return threads;
    }

    private async Task<BusMessage?> ParseMessageAsync(
        string filePath,
        bool isArchive,
        IReadOnlyCollection<string> knownPrefixes,
        CancellationToken ct)
    {
        var fileName = Path.GetFileName(filePath);

        // Determine if reply: ends with .reply.md
        bool isReply = fileName.EndsWith(".reply.md", StringComparison.OrdinalIgnoreCase);

        // Strip extension(s) to get routing name
        string baseName = isReply
            ? fileName[..^".reply.md".Length]   // strip .reply.md
            : fileName[..^".md".Length];          // strip .md

        // Find the longest matching known prefix
        string? matchedPrefix = knownPrefixes
            .Where(p => baseName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();

        string routingPrefix;
        string remainder;

        if (matchedPrefix is not null)
        {
            routingPrefix = matchedPrefix;
            remainder = baseName[matchedPrefix.Length..];
        }
        else
        {
            // Best-effort: up to (and including) the first '-'
            var dashIdx = baseName.IndexOf('-');
            if (dashIdx < 0)
                return null; // can't parse

            routingPrefix = baseName[..(dashIdx + 1)];
            remainder = baseName[(dashIdx + 1)..];
        }

        // Strip follow-up- / redirect- markers from remainder to get slug
        bool isFollowUp = remainder.StartsWith("follow-up-", StringComparison.OrdinalIgnoreCase);
        bool isRedirect = remainder.StartsWith("redirect-", StringComparison.OrdinalIgnoreCase);

        string slug = isFollowUp
            ? remainder["follow-up-".Length..]
            : isRedirect
                ? remainder["redirect-".Length..]
                : remainder;

        // Determine Kind
        bool isBroadcastPrefix = routingPrefix.Equals("all-", StringComparison.OrdinalIgnoreCase);
        BusMessageKind kind;
        if (isReply)
            kind = isBroadcastPrefix ? BusMessageKind.BroadcastReply : BusMessageKind.Reply;
        else if (isBroadcastPrefix)
            kind = BusMessageKind.Broadcast;
        else if (isFollowUp)
            kind = BusMessageKind.FollowUp;
        else
            kind = BusMessageKind.Inbox;

        // State defaults
        var state = isArchive ? BusMessageState.Archived : BusMessageState.New;

        // Read body
        string body = await File.ReadAllTextAsync(filePath, ct);

        // Parse header
        string? from = null;
        DateTimeOffset? timestamp = null;

        var fromMatch = FromPattern.Match(body);
        if (fromMatch.Success)
            from = fromMatch.Groups[1].Value.Trim();

        var tsMatch = TimestampPattern.Match(body);
        if (tsMatch.Success &&
            DateTimeOffset.TryParse(
                tsMatch.Groups[1].Value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            timestamp = parsed;
        }

        return new BusMessage(slug, routingPrefix, kind, state, filePath, timestamp, from, body);
    }
}
