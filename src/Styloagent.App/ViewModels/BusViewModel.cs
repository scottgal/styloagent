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

    /// <summary>The owning thread's key (slug), so opening a message can mark its thread SEEN.</summary>
    public string ThreadKey { get; init; } = "";

    /// <summary>The owning thread's newest-activity timestamp (the seen-watermark reference).</summary>
    public DateTimeOffset? ThreadLastActivity { get; init; }

    /// <summary>Operator has viewed the thread this message belongs to (seeds the SEEN pill).</summary>
    public bool IsSeen { get; init; }

    /// <summary>Operator explicitly archived the thread this message belongs to.</summary>
    public bool IsOperatorArchived { get; init; }

    /// <summary>Directory of the backing file, so relative links/images in the markdown resolve.</summary>
    public string SourcePath => string.IsNullOrEmpty(FilePath) ? "" : (Path.GetDirectoryName(FilePath) ?? "");

    public string RelativeTime => BusTime.Format(Timestamp);

    // ── 3-state status pill (WAITING → SEEN → DONE) ──────────────────────────────────────────────
    // WAITING/DONE come from message content (New vs Replied/Archived); SEEN is operator view-state
    // (the thread was viewed but not yet handled). An explicit operator archive counts as DONE.

    /// <summary>Handled: replied/archived by content, or explicitly archived by the operator.</summary>
    public bool IsDone => State is "Replied" or "Archived" || IsOperatorArchived;

    /// <summary>Operator viewed this (still-open) message's thread but hasn't replied/archived yet.</summary>
    public bool IsSeenState => !IsDone && State == "New" && IsSeen;

    /// <summary>The status pill label: DONE once handled, SEEN once viewed, else WAITING.</summary>
    public string StatusPillText => IsDone ? "DONE" : IsSeenState ? "SEEN" : "WAITING";

    /// <summary>Pill background — green (done), steel-blue (seen), amber (waiting).</summary>
    public string StatusPillBgHex => IsDone ? "#243024" : IsSeenState ? "#1E2A3A" : "#3A2E00";

    /// <summary>Pill foreground — green (done), steel-blue (seen), amber (waiting).</summary>
    public string StatusPillFgHex => IsDone ? "#7FB07F" : IsSeenState ? "#6FA8D6" : "#E5A05A";

    /// <summary>DONE fades most; SEEN is gently de-emphasized; WAITING stays full-strength.</summary>
    public double RowOpacity => IsDone ? 0.5 : IsSeenState ? 0.85 : 1.0;
}

/// <summary>One thread row in the attention-first bus.</summary>
public sealed partial class BusThreadItem : ObservableObject
{
    /// <summary>The thread's key (slug) — used to look up operator seen/archived view-state.</summary>
    public string Key { get; init; } = "";

    public string Glyph { get; init; } = "";
    public string Subject { get; init; } = "";
    public string ParticipantsDisplay { get; init; } = "";
    public string ColorHex { get; init; } = "#888888";
    public string RelativeTime { get; init; } = "–";

    /// <summary>The thread's newest-activity timestamp (the seen-watermark reference).</summary>
    public DateTimeOffset? LastActivity { get; init; }

    public BusThreadSection Section { get; init; }
    public IReadOnlyList<BusMessageItem> Messages { get; init; } = Array.Empty<BusMessageItem>();

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Operator has viewed this thread (open / expand) since its last activity → SEEN pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsSeenState))]
    [NotifyPropertyChangedFor(nameof(StatusPillText))]
    [NotifyPropertyChangedFor(nameof(HasStatusPill))]
    [NotifyPropertyChangedFor(nameof(StatusPillBgHex))]
    [NotifyPropertyChangedFor(nameof(StatusPillFgHex))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isSeen;

    /// <summary>Operator explicitly archived (dismissed) this thread → DONE, even if still unreplied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsSeenState))]
    [NotifyPropertyChangedFor(nameof(CanArchive))]
    [NotifyPropertyChangedFor(nameof(StatusPillText))]
    [NotifyPropertyChangedFor(nameof(HasStatusPill))]
    [NotifyPropertyChangedFor(nameof(StatusPillBgHex))]
    [NotifyPropertyChangedFor(nameof(StatusPillFgHex))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isOperatorArchived;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    // ── 3-state status pill (WAITING → SEEN → DONE) ──────────────────────────────────────────────
    // WAITING/DONE come from the content-derived Section (bus-'s classifier); SEEN is operator
    // view-state layered on top. An explicit operator archive counts as DONE.

    /// <summary>Handled: the thread reached <see cref="BusThreadSection.Archive"/>, or the operator archived it.</summary>
    public bool IsDone => Section == BusThreadSection.Archive || IsOperatorArchived;

    /// <summary>Loud: an unreplied inbound in <see cref="BusThreadSection.Attention"/> the operator hasn't viewed.</summary>
    public bool IsWaiting => !IsDone && Section == BusThreadSection.Attention && !IsSeen;

    /// <summary>The middle rung: an attention thread the operator has viewed but not yet handled.</summary>
    public bool IsSeenState => !IsDone && Section == BusThreadSection.Attention && IsSeen;

    /// <summary>Pill label: DONE (handled), SEEN (viewed), WAITING (needs a reply); empty for Recent threads.</summary>
    public string StatusPillText => IsDone ? "DONE" : IsSeenState ? "SEEN" : IsWaiting ? "WAITING" : "";

    /// <summary>Whether to show a status pill at all (Recent threads carry none).</summary>
    public bool HasStatusPill => StatusPillText.Length > 0;

    /// <summary>The explicit-archive affordance is offered only while the thread is still open (not DONE).</summary>
    public bool CanArchive => !IsDone;

    /// <summary>Pill background — green (done), steel-blue (seen), amber (waiting).</summary>
    public string StatusPillBgHex => IsDone ? "#243024" : IsSeenState ? "#1E2A3A" : "#3A2E00";

    /// <summary>Pill foreground — green (done), steel-blue (seen), amber (waiting).</summary>
    public string StatusPillFgHex => IsDone ? "#7FB07F" : IsSeenState ? "#6FA8D6" : "#E5A05A";

    /// <summary>DONE fades most; SEEN is gently de-emphasized; WAITING/Recent stay full-strength.</summary>
    public double RowOpacity => IsDone ? 0.5 : IsSeenState ? 0.85 : 1.0;
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
    // Operator-side seen/archived view-state. Today the InMemory fake; swaps 1:1 for bus-'s Core store.
    private readonly IBusViewState _viewState;

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
        ChannelProjection? projection = null,
        IBusViewState? viewState = null)
    {
        _channelRoot = channelRoot;
        _knownPrefixes = knownPrefixes;
        _projection = projection ?? new ChannelProjection();
        _viewState = viewState ?? new InMemoryBusViewState();

        // One timer instance, started as "disabled" (Timeout.Infinite).
        _debounceTimer = new Timer(_ => _ = LoadAsync(), null, Timeout.Infinite, Timeout.Infinite);

        // A view-state change (mark-seen / archive, here or — with the real store — elsewhere) refreshes
        // the feed through the same debounced reload path the FSW uses, so pills stay live.
        _viewState.Changed += ScheduleReload;

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

                    // Build attention-first thread rows, each seeded with the operator's seen/archived
                    // view-state (a separate fact from bus-'s content-derived section). Message rows are
                    // built per-thread so each carries its thread's key + view-state for the pill.
                    var built = threads.Select(t =>
                    {
                        var view = BusThreadClassifier.Classify(t);
                        string key = t.Slug;
                        var lastActivity = view.LastActivity;
                        bool seen = _viewState.IsSeen(key, lastActivity);
                        bool archived = _viewState.IsArchived(key);

                        string primaryPrefix = (t.Prefixes.Count > 0 ? t.Prefixes[0] : null)
                                               ?? (t.Messages.Count > 0 ? t.Messages[0].RoutingPrefix : null) ?? "";
                        string? from = t.Messages.Count > 0 ? t.Messages[0].From : null;
                        string participants = string.IsNullOrWhiteSpace(from)
                            ? primaryPrefix
                            : $"{from} → {primaryPrefix}";
                        var msgItems = t.Messages
                            .Select(m => BuildMessageItem(m, key, lastActivity, seen, archived))
                            .ToList();
                        var threadItem = new BusThreadItem
                        {
                            Key                 = key,
                            Glyph               = view.Glyph,
                            Subject             = view.Subject,
                            ParticipantsDisplay = participants,
                            ColorHex            = PresentationStore.DefaultColorFor(primaryPrefix),
                            RelativeTime        = BusTime.Format(view.LastActivity),
                            LastActivity        = lastActivity,
                            Section             = view.Section,
                            Messages            = msgItems,
                            IsSeen              = seen,
                            IsOperatorArchived  = archived,
                        };
                        return (threadItem, msgItems);
                    }).ToList();

                    var threadItems = built.Select(b => b.threadItem).ToList();
                    // Flatten: all messages across all threads, ordered most-recent first.
                    var items = built
                        .SelectMany(b => b.msgItems)
                        .OrderByDescending(m => m.Timestamp ?? DateTimeOffset.MinValue)
                        .ToList();

                    // Update Messages — handle both UI-thread and headless/test contexts.
                    void UpdateMessages()
                    {
                        // Preserve which threads the operator had expanded — a reload must not snap them
                        // shut. Keyed by thread slug so the rebuilt row re-opens.
                        var expandedKeys = AttentionThreads
                            .Concat(RecentThreads).Concat(ArchivedThreads)
                            .Where(t => t.IsExpanded)
                            .Select(t => t.Key)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                            if (ti.Key.Length > 0 && expandedKeys.Contains(ti.Key))
                                ti.IsExpanded = true;

                            // An explicit operator-archive tucks the thread into the Archive drawer even
                            // if its content still reads as an unreplied inbound (classifier: Attention).
                            if (ti.IsOperatorArchived)
                            {
                                ArchivedThreads.Add(ti);
                                continue;
                            }
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

    /// <summary>Double-click a message → open its full markdown; viewing it marks its thread SEEN.</summary>
    [RelayCommand]
    private void OpenMessage(BusMessageItem? item)
    {
        if (item is not { FilePath.Length: > 0 }) return;
        MarkSeenByKey(item.ThreadKey, item.ThreadLastActivity);
        OpenDocument?.Invoke(item.FilePath);
    }

    /// <summary>Popout a thread → carousel through its messages; opening it marks the thread SEEN.</summary>
    [RelayCommand]
    private void OpenThread(BusThreadItem? thread)
    {
        if (thread is null) return;
        MarkThreadSeen(thread);
        ThreadOpener?.Invoke(thread);
    }

    /// <summary>Expand/collapse a thread inline. Expanding it counts as the operator viewing it → SEEN.</summary>
    [RelayCommand]
    private void ToggleThread(BusThreadItem? thread)
    {
        if (thread is null) return;
        thread.IsExpanded = !thread.IsExpanded;
        if (thread.IsExpanded) MarkThreadSeen(thread);
    }

    /// <summary>Explicitly archive (dismiss) a thread → DONE. Persists so the reload re-sections it.</summary>
    [RelayCommand]
    private void ArchiveThread(BusThreadItem? thread)
    {
        if (thread is null || thread.Key.Length == 0) return;
        thread.IsOperatorArchived = true;      // in-place → DONE immediately
        _viewState.Archive(thread.Key);        // persist; Changed → reload re-sections into Archive
    }

    /// <summary>Mark a thread SEEN: reflect it in place (pill) and persist to the view-state store.</summary>
    private void MarkThreadSeen(BusThreadItem thread)
    {
        if (thread.Key.Length == 0) return;
        thread.IsSeen = true;                              // in-place pill update (no reload needed)
        _viewState.MarkSeen(thread.Key, thread.LastActivity);
    }

    /// <summary>Mark SEEN by thread key (from a message row), reflecting any visible attention row.</summary>
    private void MarkSeenByKey(string key, DateTimeOffset? lastActivity)
    {
        if (string.IsNullOrEmpty(key)) return;
        _viewState.MarkSeen(key, lastActivity);
        foreach (var t in AttentionThreads)
            if (string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase))
                t.IsSeen = true;
    }

    private static BusMessageItem BuildMessageItem(
        BusMessage m, string threadKey, DateTimeOffset? threadLastActivity, bool seen, bool archived) => new()
    {
        RoutingPrefix      = m.RoutingPrefix,
        Slug               = m.Slug,
        Kind               = m.Kind.ToString(),
        State              = m.State.ToString(),
        From               = m.From,
        Timestamp          = m.Timestamp,
        ColorHex           = PresentationStore.DefaultColorFor(m.RoutingPrefix),
        DisplayLine        = BuildDisplayLine(m),
        FilePath           = m.FilePath,
        Body               = m.Body,
        ThreadKey          = threadKey,
        ThreadLastActivity = threadLastActivity,
        IsSeen             = seen,
        IsOperatorArchived = archived,
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

    private void OnFileChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    /// <summary>Debounced reload: reschedule the single timer; it fires once after the quiet window.
    /// Shared by the FSW and by view-state (mark-seen / archive) changes.</summary>
    private void ScheduleReload()
    {
        if (_disposed) return;
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

        _viewState.Changed -= ScheduleReload;

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
