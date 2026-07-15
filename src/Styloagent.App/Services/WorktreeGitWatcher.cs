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

    /// <summary>
    /// Raised (on a thread-pool thread) when the watched worktree's BRANCH changes — i.e. <c>.git/HEAD</c>
    /// now points at a different <c>refs/heads/&lt;branch&gt;</c>. The argument is the new branch name, or
    /// null for a detached HEAD. This is the structured "an agent switched branch" signal, distinct from
    /// the noisy <see cref="Changed"/> (which fires for any git-dir write). Only fires on an actual change.
    /// </summary>
    public event EventHandler<string?>? BranchChanged;

    private FileSystemWatcher? _fsWatcher;
    private System.Threading.Timer? _debounceTimer;
    private const int DebounceMs = 300;
    private readonly object _lock = new();
    private volatile bool _disposed;

    /// <summary>The resolved git dir currently watched, and the last branch seen there (for change detection).</summary>
    private string? _gitDir;
    private string? _lastBranch;

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
            _gitDir = null;
            _lastBranch = null;

            if (worktreePath is null) return;

            try
            {
                string? gitDir = ResolveGitDir(worktreePath);
                if (gitDir is null) return;

                // Seed the branch so switching the watched worktree doesn't spuriously fire BranchChanged.
                _gitDir = gitDir;
                _lastBranch = ReadBranch(gitDir);

                _debounceTimer = new System.Threading.Timer(
                    _ => OnDebounceElapsed(),
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

    /// <summary>
    /// Fired once per debounce window: always raises <see cref="Changed"/> (panel refresh), and if the
    /// worktree's branch has actually changed since last seen, also raises <see cref="BranchChanged"/> —
    /// the structured signal for the "switched branch" timeline op. Comparing to the last branch keeps
    /// ordinary git-dir churn (index writes, packs) from firing BranchChanged.
    /// </summary>
    private void OnDebounceElapsed()
    {
        if (_disposed) return;
        Changed?.Invoke(this, EventArgs.Empty);

        string? gitDir;
        string? last;
        lock (_lock) { gitDir = _gitDir; last = _lastBranch; }
        if (gitDir is null) return;

        var current = ReadBranch(gitDir);
        if (!string.Equals(current, last, StringComparison.Ordinal))
        {
            lock (_lock) { if (!_disposed) _lastBranch = current; }
            if (!_disposed) BranchChanged?.Invoke(this, current);
        }
    }

    /// <summary>
    /// Reads the current branch from a git dir's <c>HEAD</c> file: the name for
    /// <c>ref: refs/heads/&lt;branch&gt;</c>, or null for a detached HEAD (a raw SHA) or an unreadable HEAD.
    /// </summary>
    internal static string? ReadBranch(string gitDir)
    {
        try
        {
            var head = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(head)) return null;
            var content = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return content.StartsWith(prefix, StringComparison.Ordinal)
                ? content[prefix.Length..].Trim()
                : null;   // detached HEAD (raw SHA) or unexpected content
        }
        catch { return null; }
    }

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
