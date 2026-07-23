namespace Styloagent.Core.Memory;

/// <summary>Debounced watcher that keeps the disposable vector index current as memory Markdown changes.</summary>
public sealed class MemoryIndexWatcher : IDisposable
{
    private readonly MemoryRagOptions _options;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private readonly SemaphoreSlim _rebuild = new(1, 1);

    public MemoryIndexWatcher(MemoryRagOptions options)
    {
        _options = options;
        _debounce = new Timer(_ => _ = RebuildAsync(), null, Timeout.Infinite, Timeout.Infinite);
        if (!Directory.Exists(options.Root)) return;
        _watcher = new FileSystemWatcher(options.Root, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Created += Changed; _watcher.Changed += Changed; _watcher.Deleted += Changed; _watcher.Renamed += Changed;
        RequestRebuild();
    }

    public void RequestRebuild() => _debounce.Change(500, Timeout.Infinite);
    private void Changed(object? sender, FileSystemEventArgs e) => RequestRebuild();
    private async Task RebuildAsync()
    {
        if (!await _rebuild.WaitAsync(0).ConfigureAwait(false)) return;
        try { await MemoryRecallService.RebuildAsync(_options).ConfigureAwait(false); }
        finally { _rebuild.Release(); }
    }
    public void Dispose() { _watcher?.Dispose(); _debounce.Dispose(); _rebuild.Dispose(); }
}
