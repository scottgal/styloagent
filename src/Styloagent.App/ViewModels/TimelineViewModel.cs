using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace Styloagent.App.ViewModels;

/// <summary>One operation on the activity timeline: when, which agent, what it did, and — for a file
/// op — the path it touched (opens the source viewer) or, for an Edit, the before/after (opens a diff).</summary>
public sealed record TimelineEntry(
    DateTimeOffset At, string Agent, string Description, string ColorHex,
    string? Path = null, string? DiffOld = null, string? DiffNew = null)
{
    /// <summary>Clock time for the row, e.g. "14:31:07".</summary>
    public string TimeText => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>True when this entry has a before/after diff to show.</summary>
    public bool HasDiff => DiffOld is not null && DiffNew is not null;

    /// <summary>True when this entry points at a file that can be opened.</summary>
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    /// <summary>True when clicking the row does something (open a diff or the file).</summary>
    public bool IsOpenable => HasDiff || HasPath;
}

/// <summary>
/// A merged, newest-first operations feed for the cockpit: each agent's hook operations (tool use,
/// attention, lifecycle) and the messages they send over the bus. Think "git history meets
/// OpenTelemetry" — how you see work flowing through the fleet. Bounded so it never grows unbounded.
/// </summary>
public sealed partial class TimelineViewModel
{
    private const int Cap = 500;

    /// <summary>Timeline entries, newest first.</summary>
    public ObservableCollection<TimelineEntry> Entries { get; } = new();

    /// <summary>Set by the owner: opens a touched file in the source viewer.</summary>
    public Action<string>? OpenSource { get; set; }

    /// <summary>Set by the owner: opens an edit's before/after as a diff.</summary>
    public Action<TimelineEntry>? OpenDiff { get; set; }

    /// <summary>Appends an operation (called on the UI thread from the hook + message feeds).</summary>
    public void Add(DateTimeOffset at, string agent, string description, string colorHex,
        string? path = null, string? diffOld = null, string? diffNew = null)
    {
        Entries.Insert(0, new TimelineEntry(at, agent, description, colorHex, path, diffOld, diffNew));
        while (Entries.Count > Cap) Entries.RemoveAt(Entries.Count - 1);
    }

    /// <summary>Opens what a timeline row points at — a diff if it has one, else the file.</summary>
    [RelayCommand]
    private void Open(TimelineEntry? entry)
    {
        if (entry is null) return;
        if (entry.HasDiff) OpenDiff?.Invoke(entry);
        else if (entry.HasPath) OpenSource!.Invoke(entry.Path!);
    }
}
