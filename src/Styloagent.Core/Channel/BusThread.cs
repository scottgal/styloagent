namespace Styloagent.Core.Channel;

public sealed record BusThread(
    string Slug,
    IReadOnlyList<BusMessage> Messages,
    IReadOnlyList<string> Prefixes);
