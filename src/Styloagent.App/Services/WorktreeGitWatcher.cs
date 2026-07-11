namespace Styloagent.App.Services;

/// <summary>
/// Watches a worktree's <c>.git</c> directory for filesystem changes and raises a debounced
/// <see cref="Changed"/> event.  Tolerant — if the path cannot be watched, <see cref="Watch"/>
/// is a silent no-op.  Handles both a regular .git directory and a linked-worktree .git file
/// (which contains a <c>gitdir: &lt;path&gt;</c> line pointing at the real git directory).
/// </summary>
public sealed class WorktreeGitWatcher : IDisposable
{
    /// <summary>
    /// Raised (on a thread-pool thread) after a debounce period following one or more filesystem
    /// changes under the watched git directory.  The VM marshals this to the UI thread.
    /// </summary>
    public event EventHandler? Changed;

    private FileSystemWatcher? _fsWatcher;
    private System.Threading.Timer? _debounceTimer;
    private const int DebounceMs = 300;
    private readonly object _lock = new();
    private volatile bool _disposed;

    /// <summary>
    /// Points the watcher at the given worktree path, replacing any previous watch.
    /// Resolves <c>.git</c> — directory or file — to find the real git dir.
    /// Pass <c>null</c> to stop watching without starting a new watch.
    /// Never throws; all errors are swallowed.
    /// </summary>
    public void Watch(string? worktreePath)
    {
        lock (_lock)
        {
            if (_disposed) return;

            DisposeInternals();

            if (worktreePath is null) return;

            try
            {
                string? gitDir = ResolveGitDir(worktreePath);
                if (gitDir is null) return;

                _debounceTimer = new System.Threading.Timer(
                    _ => { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); },
                    state: null,
                    dueTime: Timeout.Infinite,
                    period: Timeout.Infinite);

                var fsw = new FileSystemWatcher(gitDir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
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
                // If we can't watch (e.g. path gone, no permissions), clean up and no-op.
                DisposeInternals();
            }
        }
    }

    /// <summary>
    /// Resolves the git directory for a worktree.
    /// Returns the path if it is a watchable directory; null otherwise.
    /// </summary>
    private static string? ResolveGitDir(string worktreePath)
    {
        try
        {
            var dotGit = Path.Combine(worktreePath, ".git");

            if (Directory.Exists(dotGit))
                return dotGit;

            if (File.Exists(dotGit))
            {
                // Linked worktree: .git is a text file containing "gitdir: <relative-or-absolute-path>"
                foreach (var line in File.ReadLines(dotGit))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        var target = trimmed["gitdir:".Length..].Trim();
                        // May be relative to the worktree directory.
                        var resolved = Path.GetFullPath(Path.Combine(worktreePath, target));
                        return Directory.Exists(resolved) ? resolved : null;
                    }
                }
            }
        }
        catch { /* fall through to null */ }

        return null;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => ResetTimer();

    private void OnFsRenamed(object sender, RenamedEventArgs e) => ResetTimer();

    private void ResetTimer()
    {
        try
        {
            lock (_lock)
            {
                _debounceTimer?.Change(DebounceMs, Timeout.Infinite);
            }
        }
        catch { /* timer may have been disposed; no-op */ }
    }

    /// <summary>
    /// Disposes the watcher and timer; caller must hold <see cref="_lock"/> or call from Dispose.
    /// </summary>
    private void DisposeInternals()
    {
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

        if (_debounceTimer is not null)
        {
            _debounceTimer.Dispose();
            _debounceTimer = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            DisposeInternals();
        }
    }
}
