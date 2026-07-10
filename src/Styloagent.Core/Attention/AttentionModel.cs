namespace Styloagent.Core.Attention;

/// <summary>One agent as the attention router sees it.</summary>
public sealed record AttentionCandidate(string Id, bool NeedsYou, DateTimeOffset? WaitingSince);

/// <summary>Pure ordering of the agents that need the human, oldest-first.</summary>
public static class AttentionQueue
{
    public static IReadOnlyList<string> Build(IEnumerable<AttentionCandidate> candidates)
        => candidates
            .Where(c => c.NeedsYou)
            .OrderBy(c => c.WaitingSince ?? DateTimeOffset.MaxValue)   // nulls last
            .Select(c => c.Id)
            .ToList();
}

/// <summary>Pure decision: which pane (if any) to auto-reveal.</summary>
public static class AutoReveal
{
    public static string? Decide(bool humanBusy, string? queueHead, string? activeId)
        => (!humanBusy && queueHead is not null && queueHead != activeId) ? queueHead : null;
}
