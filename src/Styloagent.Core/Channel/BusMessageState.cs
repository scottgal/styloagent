namespace Styloagent.Core.Channel;

public enum BusMessageState
{
    New,
    Replied,
    Archived,
    /// <summary>
    /// The message's recipient agent is no longer in the live fleet — the thread is orphaned
    /// and needs triage (re-assign or archive). Set by the bus viewer when it cross-references
    /// the channel against the live fleet snapshot.
    /// </summary>
    Abandoned
}
