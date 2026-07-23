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

    /// <summary>The recipient has picked this message up (drained it from the MCP-native pending queue).</summary>
    public bool IsPickedUp { get; init; }

    /// <summary>Operator explicitly archived the thread this message belongs to.</summary>
    public bool IsOperatorArchived { get; init; }

    /// <summary>Directory of the backing file, so relative links/images in the markdown resolve.</summary>
    public string SourcePath => string.IsNullOrEmpty(FilePath) ? "" : (Path.GetDirectoryName(FilePath) ?? "");

    public string RelativeTime => BusTime.Format(Timestamp);

    // ── 3-state status pill (WAITING → WORKING → DONE) ───────────────────────────────────────────
    // WAITING/DONE come from message content (New vs Replied/Archived); WORKING means a recipient has
    // picked the note up (PickupProjection). An explicit operator archive counts as DONE.

    /// <summary>Handled: replied/archived by content, or explicitly archived by the operator.</summary>
    public bool IsDone => State is "Replied" or "Archived" || IsOperatorArchived;

    /// <summary>Being worked: still open, but the recipient has picked it up.</summary>
    public bool IsBeingWorked => !IsDone && State == "New" && IsPickedUp;

    /// <summary>The status pill label: DONE once handled, WORKING once picked up, else WAITING.</summary>
    public string StatusPillText => IsDone ? "DONE" : IsBeingWorked ? "WORKING" : "WAITING";

    /// <summary>Pill background — green (done), steel-blue (working), amber (waiting).</summary>
    public string StatusPillBgHex => IsDone ? "#243024" : IsBeingWorked ? "#1E2A3A" : "#3A2E00";

    /// <summary>Pill foreground — green (done), steel-blue (working), amber (waiting).</summary>
    public string StatusPillFgHex => IsDone ? "#7FB07F" : IsBeingWorked ? "#6FA8D6" : "#E5A05A";

    /// <summary>DONE fades most; WORKING is gently de-emphasized; WAITING stays full-strength.</summary>
    public double RowOpacity => IsDone ? 0.5 : IsBeingWorked ? 0.85 : 1.0;
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

    /// <summary>The "Nm ago" display. Observable + settable so an in-place reconcile (BUG 5) can refresh
    /// it on a REUSED row without churning the row's container.</summary>
    [ObservableProperty]
    private string _relativeTime = "–";

    /// <summary>The thread's newest-activity timestamp (the seen-watermark reference).</summary>
    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>The thread has an unreplied inbound that needs a human reply (drives the WAITING/WORKING pill).
    /// Carried explicitly because a SEEN thread is demoted to Recent yet still needs a reply.</summary>
    public bool NeedsReply { get; init; }

    /// <summary>A recipient has picked up the thread's open note (PickupProjection) → WORKING rather than WAITING.</summary>
    public bool IsPickedUp { get; init; }

    public BusThreadSection Section { get; set; }
    public IReadOnlyList<BusMessageItem> Messages { get; init; } = Array.Empty<BusMessageItem>();

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Operator explicitly archived (dismissed) this thread → DONE, even if still unreplied.
    /// Observable so the ✕ gesture flips the pill to DONE in place before the reload re-sections it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsBeingWorked))]
    [NotifyPropertyChangedFor(nameof(CanArchive))]
    [NotifyPropertyChangedFor(nameof(StatusPillText))]
    [NotifyPropertyChangedFor(nameof(HasStatusPill))]
    [NotifyPropertyChangedFor(nameof(StatusPillBgHex))]
    [NotifyPropertyChangedFor(nameof(StatusPillFgHex))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isOperatorArchived;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    // ── 3-state status pill (WAITING → WORKING → DONE) ───────────────────────────────────────────
    // The pill reflects HANDLING status: WAITING (needs a reply, nobody on it) → WORKING (needs a reply,
    // a recipient picked it up) → DONE (replied/archived, or operator-archived). Operator READ-state
    // (seen) is expressed separately as a SECTION demotion in BusViewModel, not as a pill.

    /// <summary>Handled: the thread reached <see cref="BusThreadSection.Archive"/>, or the operator archived it.</summary>
    public bool IsDone => Section == BusThreadSection.Archive || IsOperatorArchived;

    /// <summary>Loud: a still-open thread that needs a reply and nobody has picked up.</summary>
    public bool IsWaiting => !IsDone && NeedsReply && !IsPickedUp;

    /// <summary>The middle rung: a still-open thread that needs a reply and a recipient is working it.</summary>
    public bool IsBeingWorked => !IsDone && NeedsReply && IsPickedUp;

    /// <summary>Pill label: DONE (handled), WORKING (picked up), WAITING (needs a reply); empty otherwise.</summary>
    public string StatusPillText => IsDone ? "DONE" : IsBeingWorked ? "WORKING" : IsWaiting ? "WAITING" : "";

    /// <summary>Whether to show a status pill at all (broadcasts / in-flight Recent threads carry none).</summary>
    public bool HasStatusPill => StatusPillText.Length > 0;

    /// <summary>The explicit-archive affordance is offered only while the thread is still open (not DONE).</summary>
    public bool CanArchive => !IsDone;

    /// <summary>Pill background — green (done), steel-blue (working), amber (waiting).</summary>
    public string StatusPillBgHex => IsDone ? "#243024" : IsBeingWorked ? "#1E2A3A" : "#3A2E00";

    /// <summary>Pill foreground — green (done), steel-blue (working), amber (waiting).</summary>
    public string StatusPillFgHex => IsDone ? "#7FB07F" : IsBeingWorked ? "#6FA8D6" : "#E5A05A";

    /// <summary>DONE fades most; WORKING is gently de-emphasized; WAITING stays full-strength.</summary>
    public double RowOpacity => IsDone ? 0.5 : IsBeingWorked ? 0.85 : 1.0;
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
    // Per-message "picked up" lookup (recipient drained the note) → the WORKING pill. Keyed by
    // (filePath, routingPrefix); wired from Core.Attention.PickupProjection. Null-safe default = never.
    private readonly Func<string, string, bool> _isPickedUp;

    // The pickup signal (delivered ledger + pending push/info files) lives under the temp hooks
    // `deliver/` dir, NOT under _channelRoot — so the channel FileSystemWatcher never sees a drain and
    // the WORKING pill would freeze at WAITING. We poll this dir on a low-frequency timer (mirroring
    // HookChannel's deliberate poll-over-FSW choice for the temp hooks dir) and reproject only when its
    // fingerprint changes. Null when delivery isn't MCP-wired (nothing to track → WAITING/DONE only).
    private readonly string? _pickupWatchDir;
    private Timer? _pickupPollTimer;
    private readonly object _pickupLock = new();
    private string _pickupFingerprint = "";
    private const int PickupPollMs = 750;

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
    private ObservableCollection<BusThreadItem> _readThreads = new();

    [ObservableProperty]
    private ObservableCollection<BusThreadItem> _actionedThreads = new();

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Raised after each successful channel reload, so listeners (message delivery) can
    /// diff for new messages. Fires on whatever thread the reload completed on.</summary>
    public event Action? Reloaded;

    public BusViewModel(
        string channelRoot,
        IReadOnlyList<string> knownPrefixes,
        ChannelProjection? projection = null,
        IBusViewState? viewState = null,
        Func<string, string, bool>? isPickedUp = null,
        string? pickupWatchDir = null)
    {
        _channelRoot = channelRoot;
        _knownPrefixes = knownPrefixes;
        _projection = projection ?? new ChannelProjection();
        _viewState = viewState ?? new InMemoryBusViewState();
        _isPickedUp = isPickedUp ?? ((_, _) => false);
        _pickupWatchDir = pickupWatchDir;

        // One timer instance, started as "disabled" (Timeout.Infinite).
        _debounceTimer = new Timer(_ => _ = LoadAsync(), null, Timeout.Infinite, Timeout.Infinite);

        // A view-state change (mark-seen / archive, here or — with the real store — elsewhere) refreshes
        // the feed through the same debounced reload path the FSW uses, so pills stay live.
        _viewState.Changed += ScheduleReload;

        // Seed the pickup fingerprint so the first poll tick doesn't spuriously reload, then start the
        // low-frequency poll that turns a drain (WAITING→WORKING) into a live reprojection.
        if (_pickupWatchDir is not null)
        {
            _pickupFingerprint = ComputePickupFingerprint();
            _pickupPollTimer = new Timer(_ => PollPickupOnce(), null, PickupPollMs, PickupPollMs);
        }

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

                    // Build attention-first thread rows. Two facts layer over bus-'s pure classifier:
                    //  • operator READ-state (seen/archived, from _viewState) DEMOTES a seen-but-unreplied
                    //    thread out of NEEDS ATTENTION → Recent, so the list reflects what's been read;
                    //  • per-message PICKUP (from _isPickedUp) drives the WORKING pill.
                    var built = threads.Select(t =>
                    {
                        var view = BusThreadClassifier.Classify(t);
                        string key = t.Slug;
                        var lastActivity = view.LastActivity;
                        bool seen = _viewState.IsSeen(key, lastActivity);
                        bool archived = _viewState.IsArchived(key);
                        bool needsReply = view.Section == BusThreadSection.Attention;   // has an unreplied inbound

                        // A thread is "being worked" when its open inbound has been picked up by a recipient.
                        bool pickedUp = t.Messages.Any(m =>
                            m.Kind == BusMessageKind.Inbox && m.State == BusMessageState.New
                            && _isPickedUp(m.FilePath, m.RoutingPrefix));

                        // Effective section: an operator-archive tucks it away; a seen unreplied thread is
                        // demoted to Recent (THE fix — it stops screaming); otherwise the classifier's call.
                        BusThreadSection section =
                            archived ? BusThreadSection.Archive :
                            (needsReply && seen) ? BusThreadSection.Recent :
                            view.Section;

                        string primaryPrefix = (t.Prefixes.Count > 0 ? t.Prefixes[0] : null)
                                               ?? (t.Messages.Count > 0 ? t.Messages[0].RoutingPrefix : null) ?? "";
                        string? from = t.Messages.Count > 0 ? t.Messages[0].From : null;
                        string participants = string.IsNullOrWhiteSpace(from)
                            ? primaryPrefix
                            : $"{from} → {primaryPrefix}";
                        var msgItems = t.Messages
                            .Select(m => BuildMessageItem(m, key, lastActivity, archived))
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
                            Section             = section,
                            Messages            = msgItems,
                            NeedsReply          = needsReply,
                            IsPickedUp          = pickedUp,
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
                        // shut. Keyed by thread slug so a REPLACED row re-opens (a reused row keeps its own).
                        var expandedKeys = AttentionThreads
                            .Concat(ReadThreads).Concat(ActionedThreads)
                            .Where(t => t.IsExpanded)
                            .Select(t => t.Key)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var attention = new List<BusThreadItem>();
                        var recent = new List<BusThreadItem>();
                        var archived = new List<BusThreadItem>();
                        foreach (var ti in threadItems)
                        {
                            if (ti.Key.Length > 0 && expandedKeys.Contains(ti.Key))
                                ti.IsExpanded = true;

                            // An explicit operator-archive tucks the thread into the Archive drawer even
                            // if its content still reads as an unreplied inbound (classifier: Attention).
                            if (ti.IsOperatorArchived) { archived.Add(ti); continue; }
                            switch (ti.Section)
                            {
                                case BusThreadSection.Attention: attention.Add(ti); break;
                                case BusThreadSection.Archive:    archived.Add(ti);  break;
                                default:                          recent.Add(ti);    break;
                            }
                        }

                        // Reconcile IN PLACE (BUG 5): reuse unchanged row instances so the non-virtualizing
                        // ItemsControls keep their containers instead of Clear()+rebuilding every one on
                        // every FSW/pickup/view-state reload — the churn that rooted ~131K controls via
                        // DynamicResource/style/collection subscriptions.
                        ReconcileMessages(Messages, items);
                        ReconcileThreads(AttentionThreads, attention);
                        ReconcileThreads(ReadThreads, recent);
                        ReconcileThreads(ActionedThreads, archived);
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
        MoveThread(thread, ActionedThreads);
        _viewState.Archive(thread.Key);        // persist; Changed → reload re-sections into Archive
    }

    /// <summary>Mark a thread SEEN: persist to the view-state store. The store's Changed event triggers a
    /// reload that DEMOTES the seen-but-unreplied thread out of NEEDS ATTENTION into Recent.</summary>
    private void MarkThreadSeen(BusThreadItem thread)
    {
        if (thread.Key.Length == 0) return;
        if (AttentionThreads.Contains(thread))
            MoveThread(thread, ReadThreads);
        _viewState.MarkSeen(thread.Key, thread.LastActivity);
    }

    /// <summary>Mark SEEN by thread key (from a message row) → persist; the reload demotes it.</summary>
    private void MarkSeenByKey(string key, DateTimeOffset? lastActivity)
    {
        if (string.IsNullOrEmpty(key)) return;
        var thread = AttentionThreads.FirstOrDefault(t =>
            string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        if (thread is not null) MoveThread(thread, ReadThreads);
        _viewState.MarkSeen(key, lastActivity);
    }

    /// <summary>Moves a thread between visible lifecycle collections immediately; persistence/reprojection
    /// follows asynchronously, but the active list never appears append-only after an operator action.</summary>
    private void MoveThread(BusThreadItem thread, ObservableCollection<BusThreadItem> destination)
    {
        AttentionThreads.Remove(thread);
        ReadThreads.Remove(thread);
        ActionedThreads.Remove(thread);
        if (!destination.Contains(thread)) destination.Insert(0, thread);
    }

    private BusMessageItem BuildMessageItem(
        BusMessage m, string threadKey, DateTimeOffset? threadLastActivity, bool archived) => new()
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
        IsPickedUp         = _isPickedUp(m.FilePath, m.RoutingPrefix),
        IsOperatorArchived = archived,
    };

    private static string BuildDisplayLine(BusMessage m)
    {
        var prefix = m.From is { Length: > 0 } f ? $"{f} → " : "";
        return $"{prefix}{m.Slug}";
    }

    // ── In-place collection reconcile (BUG 5) ────────────────────────────────────────────────────
    // Reuse an existing row instance when its rendered content is unchanged, so the non-virtualizing
    // ItemsControls keep that row's container (and its nested Messages containers + expand state) across
    // reloads instead of destroying+recreating every one. Genuinely-changed rows fall through to the
    // freshly-built instance (their container churns — bounded by the real change rate, not every reload).
    // The content signatures deliberately EXCLUDE RelativeTime (would churn as "2m"→"3m" ticks; refreshed
    // in place on reuse instead) and IsExpanded (operator state, carried by reusing the instance).

    private static void ReconcileThreads(ObservableCollection<BusThreadItem> target, List<BusThreadItem> desired)
    {
        var existing = new Dictionary<string, BusThreadItem>(StringComparer.Ordinal);
        foreach (var t in target)
            if (t.Key.Length > 0) existing[t.Key] = t;

        var final = new List<BusThreadItem>(desired.Count);
        foreach (var d in desired)
        {
            if (d.Key.Length > 0 && existing.TryGetValue(d.Key, out var e) && ThreadSig(e) == ThreadSig(d))
            {
                e.RelativeTime = d.RelativeTime;   // keep "Nm ago" fresh WITHOUT churning the container
                final.Add(e);
            }
            else final.Add(d);
        }
        Reconcile(target, final);
    }

    private static void ReconcileMessages(ObservableCollection<BusMessageItem> target, List<BusMessageItem> desired)
    {
        var existing = new Dictionary<string, BusMessageItem>(StringComparer.Ordinal);
        foreach (var m in target)
            if (m.FilePath.Length > 0) existing[m.FilePath] = m;

        var final = new List<BusMessageItem>(desired.Count);
        foreach (var d in desired)
            final.Add(d.FilePath.Length > 0
                      && existing.TryGetValue(d.FilePath, out var e)
                      && MsgSig(e) == MsgSig(d)
                ? e : d);
        Reconcile(target, final);
    }

    /// <summary>Reconcile <paramref name="target"/> to <paramref name="final"/> BY REFERENCE with the
    /// minimum add/remove/move, so instances present in both keep their ItemsControl container.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, List<T> final) where T : class
    {
        var keep = new HashSet<T>(final, ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);

        for (int i = 0; i < final.Count; i++)
        {
            if (i >= target.Count) { target.Add(final[i]); continue; }
            if (ReferenceEquals(target[i], final[i])) continue;

            int idx = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], final[i])) { idx = j; break; }

            if (idx >= 0) target.Move(idx, i);
            else target.Insert(i, final[i]);
        }
    }

    private static string ThreadSig(BusThreadItem t) => string.Join('\u0001',
        t.Key, t.Glyph, t.Subject, t.ParticipantsDisplay, t.ColorHex, ((int)t.Section).ToString(),
        t.NeedsReply ? "1" : "0", t.IsPickedUp ? "1" : "0", t.IsOperatorArchived ? "1" : "0",
        string.Join('\u0002', t.Messages.Select(MsgSig)));

    private static string MsgSig(BusMessageItem m) => string.Join('\u0003',
        m.FilePath, m.State, m.IsPickedUp ? "1" : "0", m.IsOperatorArchived ? "1" : "0", m.DisplayLine);

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

    /// <summary>
    /// Poll the pickup store (the temp hooks <c>deliver/</c> dir) once: if its fingerprint changed since the
    /// last check, schedule a reload so the WAITING→WORKING pill goes live. Returns true when a change was
    /// detected (and a reload scheduled). Also driven by an internal low-frequency timer; public so tests
    /// can drive the pickup-change path deterministically without the poll interval (cf. HookChannel.ScanOnce).
    /// </summary>
    public bool PollPickupOnce()
    {
        if (_disposed || _pickupWatchDir is null) return false;
        lock (_pickupLock)
        {
            string current = ComputePickupFingerprint();
            if (current == _pickupFingerprint) return false;
            _pickupFingerprint = current;
        }
        ScheduleReload();
        return true;
    }

    /// <summary>A cheap change-signature of the pickup dir: each file's path, length and last-write tick.
    /// A drain (push/info file claimed away) or a delivery (delivered-ledger write) shifts it. Missing dir
    /// or any I/O error → empty string (degrade: no pickup change observed).</summary>
    private string ComputePickupFingerprint()
    {
        try
        {
            if (string.IsNullOrEmpty(_pickupWatchDir) || !Directory.Exists(_pickupWatchDir))
                return "";
            var sb = new System.Text.StringBuilder();
            foreach (string f in Directory.GetFiles(_pickupWatchDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var fi = new FileInfo(f);
                sb.Append(f).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

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
        _pickupPollTimer?.Dispose();
        _watcher?.Dispose();
        _reloadGate.Dispose();
    }
}
