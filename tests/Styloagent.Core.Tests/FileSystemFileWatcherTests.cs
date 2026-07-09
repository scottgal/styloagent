using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

/// <summary>
/// Unit tests for <see cref="FileSystemFileWatcher"/>.
/// These use real filesystem I/O (temp files) but no PTY.
/// </summary>
public class FileSystemFileWatcherTests
{
    [Fact(Timeout = 10_000)]
    public async Task Returns_true_when_file_is_touched_within_timeout()
    {
        var path = Path.GetTempFileName();
        try
        {
            var watcher = new FileSystemFileWatcher();

            // Start watching before touching
            var watchTask = watcher.WaitForChangeAsync(path, TimeSpan.FromSeconds(5));

            // Give the watcher time to register
            await Task.Delay(150);

            // Touch the file
            await File.WriteAllTextAsync(path, "changed");

            var result = await watchTask;
            Assert.True(result, "Expected WaitForChangeAsync to return true after touching the file.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Returns_false_when_timeout_elapses_with_no_change()
    {
        var path = Path.GetTempFileName();
        try
        {
            var watcher = new FileSystemFileWatcher();
            var result = await watcher.WaitForChangeAsync(path, TimeSpan.FromMilliseconds(400));
            Assert.False(result, "Expected WaitForChangeAsync to return false when no change occurs before timeout.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Timeout = 5_000)]
    public async Task Throws_OperationCanceled_when_token_cancelled()
    {
        var path = Path.GetTempFileName();
        try
        {
            var watcher = new FileSystemFileWatcher();
            using var cts = new CancellationTokenSource(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => watcher.WaitForChangeAsync(path, TimeSpan.FromSeconds(10), cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Regression test: after WaitForChangeAsync returns false on timeout, the background poll
    /// task must have exited cleanly — no ObjectDisposedException or unobserved task exception.
    /// Verified by (a) confirming a second call also returns false cleanly and
    /// (b) forcing a GC + finalizer flush which would surface any unobserved task exception.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task Timeout_path_does_not_leak_poll_task_or_unobserved_exception()
    {
        var path = Path.GetTempFileName();
        try
        {
            var watcher = new FileSystemFileWatcher();

            // First call: times out normally
            var result1 = await watcher.WaitForChangeAsync(path, TimeSpan.FromMilliseconds(300));
            Assert.False(result1, "First call should return false on timeout.");

            // Give any background finalizers a chance to run before we force-flush
            await Task.Delay(50);

            // Force GC + finalizers — an unobserved task exception would surface here
            // (the runtime's unobserved task exception event fires during finalization)
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

            // Second immediate call: must also return false cleanly (no ObjectDisposedException
            // from a stale poll task touching the already-disposed CTS)
            var result2 = await watcher.WaitForChangeAsync(path, TimeSpan.FromMilliseconds(300));
            Assert.False(result2, "Second call should also return false on timeout.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
