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
    MessagePriority Priority = MessagePriority.Normal);
