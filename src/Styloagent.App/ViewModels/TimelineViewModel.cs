using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace Styloagent.App.ViewModels;

/// <summary>One operation on the activity timeline: when, which agent, what it did, and (if a file
/// op) the path it touched — so the row can be clicked to open that file in the source viewer.</summary>
public sealed record TimelineEntry(DateTimeOffset At, string Agent, string Description, string ColorHex, string? Path = null)
{
    /// <summary>Clock time for the row, e.g. "14:31:07".</summary>
    public string TimeText => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>True when this entry points at a file that can be opened.</summary>
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);
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

    /// <summary>Set by the owner: opens a touched file in the source viewer when a row is clicked.</summary>
    public Action<string>? OpenSource { get; set; }

    /// <summary>Appends an operation (called on the UI thread from the hook + message feeds).</summary>
    public void Add(DateTimeOffset at, string agent, string description, string colorHex, string? path = null)
    {
        Entries.Insert(0, new TimelineEntry(at, agent, description, colorHex, path));
        while (Entries.Count > Cap) Entries.RemoveAt(Entries.Count - 1);
    }

    /// <summary>Opens the file a timeline row points at (bound to the row's click).</summary>
    [RelayCommand]
    private void Open(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) OpenSource?.Invoke(path);
    }
}
