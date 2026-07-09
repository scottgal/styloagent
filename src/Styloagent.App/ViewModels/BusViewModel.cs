using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Styloagent.App.Config;
using Styloagent.Core.Channel;

namespace Styloagent.App.ViewModels;

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

    public string RelativeTime =>
        Timestamp.HasValue
            ? FormatRelative(DateTimeOffset.UtcNow - Timestamp.Value)
            : "–";

    private static string FormatRelative(TimeSpan elapsed) => elapsed switch
    {
        { TotalSeconds: < 60 } => $"{(int)elapsed.TotalSeconds}s ago",
        { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
        { TotalHours: < 24 }   => $"{(int)elapsed.TotalHours}h ago",
        _                       => $"{(int)elapsed.TotalDays}d ago",
    };
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
    private Timer? _debounceTimer;
    private bool _disposed;
    private const int DebounceMs = 200;

    [ObservableProperty]
    private ObservableCollection<BusMessageItem> _messages = new();

    [ObservableProperty]
    private bool _isLoading;

    public BusViewModel(
        string channelRoot,
        IReadOnlyList<string> knownPrefixes,
        ChannelProjection? projection = null)
    {
        _channelRoot = channelRoot;
        _knownPrefixes = knownPrefixes;
        _projection = projection ?? new ChannelProjection();

        _ = LoadAsync();
        StartWatcher();
    }

    /// <summary>
    /// Load (or reload) the bus feed.  Safe to call from any thread.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_channelRoot))
            return;

        try
        {
            IsLoading = true;
            var threads = await _projection.ReadAsync(_channelRoot, _knownPrefixes, ct);

            // Flatten: all messages across all threads, ordered most-recent first
            var items = threads
                .SelectMany(t => t.Messages)
                .OrderByDescending(m => m.Timestamp ?? DateTimeOffset.MinValue)
                .Select(m => new BusMessageItem
                {
                    RoutingPrefix = m.RoutingPrefix,
                    Slug          = m.Slug,
                    Kind          = m.Kind.ToString(),
                    State         = m.State.ToString(),
                    From          = m.From,
                    Timestamp     = m.Timestamp,
                    ColorHex      = PresentationStore.DefaultColorFor(m.RoutingPrefix),
                    DisplayLine   = BuildDisplayLine(m),
                })
                .ToList();

            // Update Messages - handle both UI thread and headless/test contexts
            void UpdateMessages()
            {
                Messages.Clear();
                foreach (var item in items)
                    Messages.Add(item);
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
                // No UI thread (headless test context): update directly
                UpdateMessages();
            }
        }
        catch (OperationCanceledException) { /* swallow */ }
        catch { /* degrade gracefully */ IsLoading = false; }
    }

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
        // Debounce: reset the timer on each event; fire once after DebounceMs quiet.
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => _ = LoadAsync(), null, DebounceMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer?.Dispose();
        _watcher?.Dispose();
    }
}
