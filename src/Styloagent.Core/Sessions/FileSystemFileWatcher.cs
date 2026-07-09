using Styloagent.Core.Abstractions;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Watches a file for changes using <see cref="FileSystemWatcher"/> with a last-write-time
/// poll fallback (important on macOS where FSW can be unreliable).
/// Returns <c>true</c> if the file changed within <paramref name="timeout"/>,
/// <c>false</c> if the timeout elapsed with no change, or throws <see cref="OperationCanceledException"/>
/// if <paramref name="ct"/> is cancelled.
/// </summary>
public sealed class FileSystemFileWatcher : IFileWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public async Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException($"Cannot determine directory for path '{path}'.", nameof(path));
        var fileName = Path.GetFileName(path);

        var baselineWriteTime = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : DateTime.MinValue;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        FileSystemEventHandler onChanged = (_, _) => changed.TrySetResult(true);
        RenamedEventHandler onRenamed = (_, _) => changed.TrySetResult(true);

        watcher.Changed += onChanged;
        watcher.Created += onChanged;
        watcher.Renamed += onRenamed;

        // Poll fallback: check last-write-time at PollInterval until changed or timeout.
        // Captured so we can await it in the finally block to avoid touching a disposed CTS.
        var pollTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var current = File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : DateTime.MinValue;
                if (current != baselineWriteTime)
                {
                    changed.TrySetResult(true);
                    return;
                }

                try
                {
                    await Task.Delay(PollInterval, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        try
        {
            // Register cancellation → signal the TCS so WhenAny unblocks
            await using var reg = cts.Token.Register(() => changed.TrySetCanceled(cts.Token));

            try
            {
                await changed.Task.ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our internal timeout fired — return false
                return false;
            }
        }
        finally
        {
            watcher.Changed -= onChanged;
            watcher.Created -= onChanged;
            watcher.Renamed -= onRenamed;

            // Cancel the linked CTS so the poll loop exits, then await it before
            // disposing — avoids the poll loop touching a disposed CTS.
            await cts.CancelAsync().ConfigureAwait(false);
            try { await pollTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
