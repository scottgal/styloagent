using System.Collections.ObjectModel;
using System.Globalization;

namespace Styloagent.App.ViewModels;

/// <summary>One operation on the activity timeline: when, which agent, and what it did.</summary>
public sealed record TimelineEntry(DateTimeOffset At, string Agent, string Description, string ColorHex)
{
    /// <summary>Clock time for the row, e.g. "14:31:07".</summary>
    public string TimeText => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// A merged, newest-first operations feed for the cockpit: each agent's hook operations (tool use,
/// attention, lifecycle) and the messages they send over the bus. Think "git history meets
/// OpenTelemetry" — when six agents run at once, this is how you see work flowing through the fleet.
/// Bounded so it never grows without limit.
/// </summary>
public sealed class TimelineViewModel
{
    private const int Cap = 500;

    /// <summary>Timeline entries, newest first.</summary>
    public ObservableCollection<TimelineEntry> Entries { get; } = new();

    /// <summary>Appends an operation (called on the UI thread from the hook + message feeds).</summary>
    public void Add(DateTimeOffset at, string agent, string description, string colorHex)
    {
        Entries.Insert(0, new TimelineEntry(at, agent, description, colorHex));
        while (Entries.Count > Cap) Entries.RemoveAt(Entries.Count - 1);
    }
}
