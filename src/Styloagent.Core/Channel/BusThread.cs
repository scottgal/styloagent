namespace Styloagent.Core.Channel;

public sealed record BusThread(
    string Slug,
    IReadOnlyList<BusMessage> Messages,
    IReadOnlyList<string> Prefixes)
{
    /// <summary>Stable display-state identity. Unlike <see cref="Slug"/>, it includes the recipient
    /// routing prefix so two agents can safely receive the same subject.</summary>
    public string Key { get; init; } = Slug;
}
