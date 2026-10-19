namespace Styloagent.Core.Abstractions;

public interface IFileWatcher
{
    /// <summary>
    /// Arms observation before returning the task, so a subsequent operation can safely trigger the
    /// change. Supports <see cref="Timeout.InfiniteTimeSpan"/> and releases observation on cancellation.
    /// </summary>
    Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default);
}
