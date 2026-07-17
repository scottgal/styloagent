namespace Styloagent.Core.Channel;

public sealed record BusMessage(
    string Slug,
    string RoutingPrefix,
    BusMessageKind Kind,
    BusMessageState State,
    string FilePath,
    DateTimeOffset? Timestamp,
    string? From,
    string Body,
    MessagePriority Priority = MessagePriority.Normal,
    // Bug A (repo-qualified messaging): the sending repo, parsed from the optional **From-Repo:** header.
    // Null for single-repo traffic (the header is absent) — the routing/dedupe key is still the canonical
    // repoRoot naming the channel this message lives in; FromRepo only tells a cross-repo reply where home is.
    string? FromRepo = null);
