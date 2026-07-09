namespace Styloagent.Core.Abstractions;

public interface IFileWatcher
{
    Task<bool> WaitForChangeAsync(string path, TimeSpan timeout, CancellationToken ct = default);
}
