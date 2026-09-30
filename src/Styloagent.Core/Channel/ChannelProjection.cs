using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Channel;

// CA1822: methods below are intentionally instance members (stateless service kept instantiable
// for the `new X().M()` call pattern used across the app/tests); do not make static.
#pragma warning disable CA1822

public sealed class ChannelProjection
{
    private sealed record CachedMessage(
        long Length, long LastWriteTicks, bool IsArchive, string PrefixFingerprint, BusMessage Message);

    // A large, long-lived fleet can have thousands of immutable archived Markdown messages. Most refreshes
    // change one file; rereading and regex-parsing the entire archive on each FSW event caused multi-second
    // CPU/allocation bursts. Cache immutable parses by filesystem signature and evict paths that disappeared.
    private readonly ConcurrentDictionary<string, CachedMessage> _messageCache =
        new(StringComparer.Ordinal);

    private static readonly Regex FromPattern =
        new(@"^\*\*From:\*\*\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Bug A (repo-qualified messaging): the optional **From-Repo:** header naming the sending repo, so a
    // cross-repo reply routes home. Absent for single-repo traffic. Distinct from **From:** (the `From:`
    // pattern requires ':' right after "From", so it never matches the "From-Repo:" line).
    private static readonly Regex FromRepoPattern =
        new(@"^\*\*From-Repo:\*\*\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TimestampPattern =
        new(@"^\*\*Timestamp:\*\*\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PriorityPattern =
        new(@"^\*\*Priority:\*\*\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public async Task<IReadOnlyList<BusThread>> ReadAsync(
        string channelRoot,
        IReadOnlyCollection<string> knownPrefixes,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(channelRoot))
            return Array.Empty<BusThread>();

        var allMessages = new List<BusMessage>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        string prefixFingerprint = string.Join('\u001e', knownPrefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

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
                seenFiles.Add(filePath);
                var msg = await ParseMessageCachedAsync(
                    filePath, isArchive, knownPrefixes, prefixFingerprint, ct).ConfigureAwait(false);
                if (msg is not null)
                    allMessages.Add(msg);
            }
        }

        // Moves/deletes change the path. Keeping those cache entries forever would turn the optimization
        // into a slow memory leak in a busy fleet.
        foreach (var cachedPath in _messageCache.Keys)
            if (!seenFiles.Contains(cachedPath))
                _messageCache.TryRemove(cachedPath, out _);

        // Thread identity is recipient-prefix + slug. Slugs are human-friendly subjects and routinely
        // collide ("status", "review", ...); grouping on a slug alone lets one recipient's completion
        // mark another recipient's task done. Replies are associated with the inbox addressed to their
        // **From:** agent, with legacy filename-prefix matching as a fallback.
        var inboxes = allMessages.Where(m => m.Kind == BusMessageKind.Inbox).ToList();
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in allMessages)
        {
            if (message.Kind is not (BusMessageKind.Reply or BusMessageKind.BroadcastReply))
            {
                keys[message.FilePath] = ThreadKey(message.RoutingPrefix, message.Slug);
                continue;
            }

            var raw = ReplyBaseName(message.FilePath);
            var stripped = StripKnownPrefix(raw, knownPrefixes);
            var candidates = inboxes.Where(i =>
                    (i.Slug.Equals(raw, StringComparison.OrdinalIgnoreCase) || i.Slug.Equals(stripped, StringComparison.OrdinalIgnoreCase))
                    && (i.RoutingPrefix.Equals("all-", StringComparison.OrdinalIgnoreCase)    // broadcast: match on slug only
                        || (!string.IsNullOrWhiteSpace(message.From)
                            ? i.RoutingPrefix.Equals(message.From, StringComparison.OrdinalIgnoreCase)
                            : i.RoutingPrefix.Equals(message.RoutingPrefix, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            var inbox = candidates.Count == 1 ? candidates[0] : null;
            var key = inbox is null ? ThreadKey(message.RoutingPrefix, message.Slug) : ThreadKey(inbox.RoutingPrefix, inbox.Slug);
            keys[message.FilePath] = key;
            if (inbox is not null) completed.Add(key);
        }

        allMessages = allMessages.Select(m =>
            m.Kind == BusMessageKind.Inbox && m.State == BusMessageState.New && completed.Contains(keys[m.FilePath])
                ? m with { State = BusMessageState.Replied }
                : m).ToList();

        var threads = allMessages
            .GroupBy(m => keys[m.FilePath], StringComparer.OrdinalIgnoreCase)
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

                return new BusThread(messages[0].Slug, messages, prefixes) { Key = g.Key };
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

    private async Task<BusMessage?> ParseMessageCachedAsync(
        string filePath,
        bool isArchive,
        IReadOnlyCollection<string> knownPrefixes,
        string prefixFingerprint,
        CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        long length = info.Length;
        long lastWriteTicks = info.LastWriteTimeUtc.Ticks;

        if (_messageCache.TryGetValue(filePath, out var cached)
            && cached.Length == length
            && cached.LastWriteTicks == lastWriteTicks
            && cached.IsArchive == isArchive
            && cached.PrefixFingerprint == prefixFingerprint)
            return cached.Message;

        var message = await ParseMessageAsync(filePath, isArchive, knownPrefixes, ct).ConfigureAwait(false);
        if (message is not null)
            _messageCache[filePath] = new CachedMessage(
                length, lastWriteTicks, isArchive, prefixFingerprint, message);
        return message;
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
            {
                // A documented reply is <thread>.reply.md. A one-word thread ("status") has no
                // routing dash at all, but its **From:** header identifies the recipient inbox during
                // reply resolution above. Dropping it here made that completion stay ACTIVE forever.
                if (!isReply) return null;
                routingPrefix = string.Empty;
                remainder = baseName;
            }
            else
            {
                routingPrefix = baseName[..(dashIdx + 1)];
                remainder = baseName[(dashIdx + 1)..];
            }
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

        // Read body. ConfigureAwait(false): this is Core I/O — it must NOT capture a caller's
        // SynchronizationContext. When invoked from the UI thread (BusViewModel.LoadAsync), a
        // captured context makes every read continuation depend on the UI thread pumping it; under
        // the shared single-threaded headless test session that pile-up deadlocks.
        string body = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);

        // Parse header
        string? from = null;
        DateTimeOffset? timestamp = null;

        var fromMatch = FromPattern.Match(body);
        if (fromMatch.Success)
            from = fromMatch.Groups[1].Value.Trim();

        var fromRepoMatch = FromRepoPattern.Match(body);
        string? fromRepo = fromRepoMatch.Success ? fromRepoMatch.Groups[1].Value.Trim() : null;

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

        var priorityMatch = PriorityPattern.Match(body);
        var priority = priorityMatch.Success
            ? ParsePriority(priorityMatch.Groups[1].Value)
            : MessagePriority.Normal;

        return new BusMessage(slug, routingPrefix, kind, state, filePath, timestamp, from, body, priority, fromRepo);
    }

    private static string ReplyBaseName(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return name.EndsWith(".reply.md", StringComparison.OrdinalIgnoreCase)
            ? name[..^".reply.md".Length]
            : name;
    }

    private static string StripKnownPrefix(string value, IReadOnlyCollection<string> knownPrefixes)
        => knownPrefixes.Where(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .Select(p => value[p.Length..])
            .FirstOrDefault() ?? value;

    private static string ThreadKey(string prefix, string slug)
        => $"{prefix.Trim().ToLowerInvariant()}\u001f{slug.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Maps a <c>**Priority:**</c> header value to a <see cref="MessagePriority"/>. Case-insensitive
    /// and tolerant: anything unrecognized (or empty) falls back to <see cref="MessagePriority.Normal"/>.
    /// </summary>
    internal static MessagePriority ParsePriority(string raw) =>
        raw.Trim().ToLowerInvariant() switch
        {
            "urgent" => MessagePriority.Urgent,
            "normal" => MessagePriority.Normal,
            "low"    => MessagePriority.Low,
            "info" or "informational" => MessagePriority.Info,
            _ => MessagePriority.Normal,
        };
}
