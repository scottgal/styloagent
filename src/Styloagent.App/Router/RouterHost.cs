using Styloagent.Core.Router;

namespace Styloagent.App.Router;

/// <summary>
/// Lifecycle host that drives <see cref="RouterCoordinator.Tick"/> on a periodic timer and on
/// file-system changes under <paramref name="routerRoot"/>, passing each applied
/// <see cref="RouterDecision"/> to the supplied callback.
///
/// Dispose-race-safe: a <c>volatile bool _disposed</c> is checked inside every timer/watcher
/// callback so nothing fires after <see cref="Dispose"/>; both timers and the watcher are
/// disposed under <see cref="_lock"/>.  Mirrors the pattern in <c>WorktreeGitWatcher</c>.
///
/// Tolerant: never throws from callbacks — <see cref="RouterCoordinator.Tick"/> is itself
/// tolerant, and the callback is wrapped in try/catch.  If the root cannot be watched
/// (e.g. it doesn't exist yet), the watcher setup is skipped silently; the timer-path still
/// works.
/// </summary>
public sealed class RouterHost : IDisposable
{
    private readonly string _root;
    private readonly Action<RouterDecision> _onDecision;
    private readonly object _lock = new();
    private volatile bool _disposed;

    private System.Threading.Timer? _intervalTimer;
    private System.Threading.Timer? _debounceTimer;
    private FileSystemWatcher? _fsWatcher;

    private const int IntervalMs = 2000;
    private const int DebounceMs = 300;

    /// <summary>
    /// Creates and starts a <see cref="RouterHost"/> watching <paramref name="routerRoot"/>.
    /// </summary>
    /// <param name="routerRoot">Root of the router ledger (the directory that contains env subdirs).</param>
    /// <param name="onDecision">Callback invoked for each <see cref="RouterDecision"/> applied by a tick.</param>
    public RouterHost(string routerRoot, Action<RouterDecision> onDecision)
    {
        _root = routerRoot;
        _onDecision = onDecision;

        // Ensure root exists so both the coordinator and the FSW have something to look at.
        try { Directory.CreateDirectory(_root); } catch { }

        // Interval timer: dueTime=0 fires first tick immediately (important for tests).
        _intervalTimer = new System.Threading.Timer(
            _ => RunTick(),
            state: null,
            dueTime: 0,
            period: IntervalMs);

        // Debounce timer: starts stopped; reset on every FS event.
        _debounceTimer = new System.Threading.Timer(
            _ => RunTick(),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);

        // FileSystemWatcher: tolerant — if the root isn't watchable, skip silently.
        try
        {
            var fsw = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                Filter = "*",
                EnableRaisingEvents = true,
            };
            fsw.Changed += OnFsEvent;
            fsw.Created += OnFsEvent;
            fsw.Deleted += OnFsEvent;
            fsw.Renamed += OnFsRenamed;
            _fsWatcher = fsw;
        }
        catch
        {
            // Root may not exist or permissions may prevent watching — FSW is optional.
            _fsWatcher = null;
        }
    }

    // ── Tick ────────────────────────────────────────────────────────────────────

    private void RunTick()
    {
        // Read the disposed flag under the lock, matching ResetDebounce's discipline, so a Dispose
        // racing the timer callback is visible before we start a tick. (The callback is tolerant and
        // the tick is idempotent, so the residual window is harmless, but keep the pattern uniform.)
        lock (_lock)
        {
            if (_disposed) return;
        }
        try
        {
            foreach (var d in RouterCoordinator.Tick(_root, DateTimeOffset.UtcNow))
            {
                if (_disposed) return;
                try { _onDecision(d); }
                catch { /* callback must never propagate */ }
            }
        }
        catch { /* coordinator is already tolerant, but belt-and-suspenders */ }
    }

    // ── FSW callbacks ────────────────────────────────────────────────────────────

    private void OnFsEvent(object sender, FileSystemEventArgs e) => ResetDebounce();
    private void OnFsRenamed(object sender, RenamedEventArgs e) => ResetDebounce();

    private void ResetDebounce()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                _debounceTimer?.Change(DebounceMs, Timeout.Infinite);
            }
        }
        catch { /* timer may have been disposed; no-op */ }
    }

    // ── Dispose ──────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_fsWatcher is not null)
            {
                _fsWatcher.EnableRaisingEvents = false;
                _fsWatcher.Changed -= OnFsEvent;
                _fsWatcher.Created -= OnFsEvent;
                _fsWatcher.Deleted -= OnFsEvent;
                _fsWatcher.Renamed -= OnFsRenamed;
                _fsWatcher.Dispose();
                _fsWatcher = null;
            }

            _intervalTimer?.Dispose();
            _intervalTimer = null;

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
