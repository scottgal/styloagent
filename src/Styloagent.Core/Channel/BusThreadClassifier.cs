namespace Styloagent.Core.Channel;

/// <summary>Which bus section a thread belongs to, attention-first.</summary>
public enum BusThreadSection
{
    Attention,
    Recent,
    Archive,
}

/// <summary>A thread classified for display: its section, status glyph, subject and last activity.</summary>
public sealed record BusThreadView(
    BusThread Thread,
    BusThreadSection Section,
    string Glyph,
    string Subject,
    DateTimeOffset? LastActivity);

/// <summary>
/// Pure, message-derived classification of a <see cref="BusThread"/> for the attention-first bus.
/// Agent state (e.g. WaitingForHuman) is deliberately NOT considered here — that is surfaced in the
/// roster — so this stays a pure function of the thread's messages.
/// </summary>
public static class BusThreadClassifier
{
    public static BusThreadView Classify(BusThread thread)
    {
        var messages = thread.Messages;

        bool allArchived = messages.Count > 0 && messages.All(m => m.State == BusMessageState.Archived);
        bool hasUnrepliedInbox = messages.Any(m => m.Kind == BusMessageKind.Inbox && m.State == BusMessageState.New);
        bool hasReplied = messages.Any(m => m.State == BusMessageState.Replied);
        bool hasBroadcast = messages.Any(m => m.Kind is BusMessageKind.Broadcast or BusMessageKind.BroadcastReply);

        BusThreadSection section =
            allArchived ? BusThreadSection.Archive :
            hasUnrepliedInbox ? BusThreadSection.Attention :
            BusThreadSection.Recent;

        string glyph =
            section == BusThreadSection.Archive ? "▤" :
            hasUnrepliedInbox ? "●" :
            hasReplied ? "↩" :
            hasBroadcast ? "◆" :
            "○";

        var max = messages
            .Select(m => m.Timestamp)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        DateTimeOffset? lastActivity = max == DateTimeOffset.MinValue ? null : max;

        return new BusThreadView(thread, section, glyph, Prettify(thread.Slug), lastActivity);
    }

    private static string Prettify(string slug)
        => string.IsNullOrWhiteSpace(slug) ? slug : slug.Replace('-', ' ').Replace('_', ' ').Trim();
}
