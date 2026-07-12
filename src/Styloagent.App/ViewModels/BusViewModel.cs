using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.App.Config;
using Styloagent.Core.Channel;

namespace Styloagent.App.ViewModels;

/// <summary>Shared relative-time formatter for bus rows.</summary>
internal static class BusTime
{
    public static string Format(DateTimeOffset? ts)
        => ts.HasValue ? FormatRelative(DateTimeOffset.UtcNow - ts.Value) : "–";

    private static string FormatRelative(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 60 } => $"{(int)elapsed.TotalSeconds}s ago",
        { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
        { TotalHours: < 24 }   => $"{(int)elapsed.TotalHours}h ago",
        _                       => $"{(int)elapsed.TotalDays}d ago",
    };
}

/// <summary>
/// A single flattened message row for the live bus feed UI.
/// </summary>
public sealed class BusMessageItem
{
    public string RoutingPrefix { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Kind { get; init; } = "";
    public string State { get; init; } = "";
    public string? From { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string ColorHex { get; init; } = "#888888";
    public string DisplayLine { get; init; } = "";

    /// <summary>The message's backing <c>.md</c> file (double-click a message to open it in full).</summary>
    public string FilePath { get; init; } = "";

    /// <summary>The message body (markdown) — rendered per page in the thread carousel.</summary>
    public string Body { get; init; } = "";

    /// <summary>Directory of the backing file, so relative links/images in the markdown resolve.</summary>
    public string SourcePath => string.IsNullOrEmpty(FilePath) ? "" : (Path.GetDirectoryName(FilePath) ?? "");

    public string RelativeTime => BusTime.Format(Timestamp);
}

/// <summary>One thread row in the attention-first bus.</summary>
public sealed partial class BusThreadItem : ObservableObject
{
    public string Glyph { get; init; } = "";
    public string Subject { get; init; } = "";
    public string ParticipantsDisplay { get; init; } = "";
    public string ColorHex { get; init; } = "#888888";
    public string RelativeTime { get; init; } = "–";
    public BusThreadSection Section { get; init; }
    public IReadOnlyList<BusMessageItem> Messages { get; init; } = Array.Empty<BusMessageItem>();

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

/// <summary>
/// Live graphical signal-bus feed ViewModel.
/// Reads the channel via <see cref="ChannelProjection"/>, flattens threads into
/// <see cref="Messages"/> (most-recent first), and watches for filesystem changes
/// to keep the feed live.
/// </summary>
public sealed partial class BusViewModel : ObservableObject, IDisposable
{
    private readonly string _channelRoot;
    private readonly IReadOnlyList<string> _knownPrefixes;
    private readonly ChannelProjection _projection;

    private FileSystemWatcher? _watcher;
    // Single long-lived timer; changed on each FSW event to coalesce rapid bursts.
    private readonly Timer _debounceTimer;
    private readonly object _timerLock = new();
    // Ensures only one LoadAsync body runs at a time; coalesces queued reloads.
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private volatile bool _reloadRequested;
    private volatile bool _disposed;
    private const int DebounceMs = 200;

    [ObservableProperty]
    private ObservableCollection<BusMessageItem> _messages = new();

    [ObservableProperty]
    private ObservableCollection<BusThreadItem> _attentionThreads = new();

    [ObservableProperty]
    private ObservableCollection<BusThreadItem> _recentThreads = new();

    [ObservableProperty]
    private ObservableCollection<BusThreadItem> _archivedThreads = new();

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Raised after each successful channel reload, so listeners (message delivery) can
    /// diff for new messages. Fires on whatever thread the reload completed on.</summary>
    public event Action? Reloaded;

    public BusViewModel(
        string channelRoot,
        IReadOnlyList<string> knownPrefixes,
        ChannelProjection? projection = null)
    {
        _channelRoot = channelRoot;
        _knownPrefixes = knownPrefixes;
        _projection = projection ?? new ChannelProjection();

        // One timer instance, started as "disabled" (Timeout.Infinite).
        _debounceTimer = new Timer(_ => _ = LoadAsync(), null, Timeout.Infinite, Timeout.Infinite);

        _ = LoadAsync();
        StartWatcher();
    }

    /// <summary>
    /// Load (or reload) the bus feed.  Safe to call from any thread.
    /// Single-flighted: if a reload is already running a second one is coalesced
    /// so that exactly one more runs after the current one completes.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (!Directory.Exists(_channelRoot))
            return;

        // If we can't enter immediately, mark that another reload is wanted.
        if (!await _reloadGate.WaitAsync(0, ct))
        {
            _reloadRequested = true;
            return;
        }

        try
        {
            // Loop so that a coalesced request triggers one more run.
            do
            {
                _reloadRequested = false;
                if (_disposed) return;

                try
                {
                    IsLoading = true;
                    var threads = await _projection.ReadAsync(_channelRoot, _knownPrefixes, ct);

                    // Flatten: all messages across all threads, ordered most-recent first
                    var items = threads
                        .SelectMany(t => t.Messages)
                        .OrderByDescending(m => m.Timestamp ?? DateTimeOffset.MinValue)
                        .Select(BuildMessageItem)
                        .ToList();

                    // Build attention-first thread rows.
                    var threadItems = threads.Select(t =>
                    {
                        var view = BusThreadClassifier.Classify(t);
                        string primaryPrefix = (t.Prefixes.Count > 0 ? t.Prefixes[0] : null)
                                               ?? (t.Messages.Count > 0 ? t.Messages[0].RoutingPrefix : null) ?? "";
                        string? from = t.Messages.Count > 0 ? t.Messages[0].From : null;
                        string participants = string.IsNullOrWhiteSpace(from)
                            ? primaryPrefix
                            : $"{from} → {primaryPrefix}";
                        var msgItems = t.Messages.Select(BuildMessageItem).ToList();
                        return new BusThreadItem
                        {
                            Glyph               = view.Glyph,
                            Subject             = view.Subject,
                            ParticipantsDisplay = participants,
                            ColorHex            = PresentationStore.DefaultColorFor(primaryPrefix),
                            RelativeTime        = BusTime.Format(view.LastActivity),
                            Section             = view.Section,
                            Messages            = msgItems,
                        };
                    }).ToList();

                    // Update Messages — handle both UI-thread and headless/test contexts.
                    void UpdateMessages()
                    {
                        Messages.Clear();
                        foreach (var item in items)
                        {
                            Messages.Add(item);
                        }
                        AttentionThreads.Clear();
                        RecentThreads.Clear();
                        ArchivedThreads.Clear();
                        foreach (var ti in threadItems)
                        {
                            switch (ti.Section)
                            {
                                case BusThreadSection.Attention: AttentionThreads.Add(ti); break;
                                case BusThreadSection.Archive:    ArchivedThreads.Add(ti);  break;
                                default:                          RecentThreads.Add(ti);    break;
                            }
                        }
                        IsLoading = false;
                    }

                    try
                    {
                        if (Dispatcher.UIThread.CheckAccess())
                        {
                            UpdateMessages();
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(UpdateMessages);
                        }
                    }
                    catch
                    {
                        // No UI thread (headless test context): update directly.
                        UpdateMessages();
                    }

                    // Signal listeners (e.g. message delivery) that the channel was re-read.
                    Reloaded?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[BusViewModel] bus refresh failed: {ex}");
                    IsLoading = false;
                }
            }
            while (_reloadRequested && !_disposed);
        }
        finally
        {
            // Guard against Dispose() having been called while we held the gate.
            if (!_disposed)
                _reloadGate.Release();
        }
    }

    // ── Open a message / thread as documents (double-click a message; popout a thread carousel) ──

    /// <summary>Opens a single message's backing markdown file as a document. Wired by the shell.</summary>
    public Action<string>? OpenDocument { get; set; }

    /// <summary>Opens a whole thread as a carousel document (page through its messages). Wired by the shell.</summary>
    public Action<BusThreadItem>? ThreadOpener { get; set; }

    /// <summary>Double-click a message → open its full markdown.</summary>
    [RelayCommand]
    private void OpenMessage(BusMessageItem? item)
    {
        if (item is { FilePath.Length: > 0 }) OpenDocument?.Invoke(item.FilePath);
    }

    /// <summary>Popout a thread → carousel through its messages.</summary>
    [RelayCommand]
    private void OpenThread(BusThreadItem? thread)
    {
        if (thread is not null) ThreadOpener?.Invoke(thread);
    }

    private static BusMessageItem BuildMessageItem(BusMessage m) => new()
    {
        RoutingPrefix = m.RoutingPrefix,
        Slug          = m.Slug,
        Kind          = m.Kind.ToString(),
        State         = m.State.ToString(),
        From          = m.From,
        Timestamp     = m.Timestamp,
        ColorHex      = PresentationStore.DefaultColorFor(m.RoutingPrefix),
        DisplayLine   = BuildDisplayLine(m),
        FilePath      = m.FilePath,
        Body          = m.Body,
    };

    private static string BuildDisplayLine(BusMessage m)
    {
        var prefix = m.From is { Length: > 0 } f ? $"{f} → " : "";
        return $"{prefix}{m.Slug}";
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(_channelRoot))
            return;

        try
        {
            _watcher = new FileSystemWatcher(_channelRoot)
            {
                IncludeSubdirectories = true,
                Filter = "*.md",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
        catch
        {
            // If the watcher can't be set up, degrade gracefully.
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        // Debounce: reschedule the single timer on each event; fires once after quiet.
        lock (_timerLock)
        {
            if (_disposed) return;
            _debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Disable the timer before disposing so in-flight callbacks see _disposed == true.
        lock (_timerLock)
        {
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        _debounceTimer.Dispose();
        _watcher?.Dispose();
        _reloadGate.Dispose();
    }
}
