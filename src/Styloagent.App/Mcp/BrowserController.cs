using Styloagent.App.Browser;
using Styloagent.App.ViewModels;
using Styloagent.Core.Browser;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>Coordinates durable browser jobs and starts an isolated runner only after owner approval.</summary>
public sealed class BrowserController : IBrowserController
{
    private readonly MainWindowViewModel _vm;
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private BrowserJobService? _service;
    private string? _serviceKey;

    public BrowserController(MainWindowViewModel vm) => _vm = vm;

    public Task<string> RequestAsync(string caller, string environment, string mode, string purpose,
        string relativePath, string? selector, bool fullPage, string? credentialRef)
    {
        var service = Service();
        return Task.FromResult(service is null ? "no active project" : service.Request(caller, environment, mode,
            purpose, relativePath, selector, fullPage, credentialRef, DateTimeOffset.UtcNow).Message);
    }

    public Task<string> ApproveAsync(string caller, string requestId)
    {
        var service = Service();
        if (service is null) return Task.FromResult("no active project");
        var result = service.Approve(caller, requestId, DateTimeOffset.UtcNow);
        if (!result.Success || result.Job is null) return Task.FromResult(result.Message);
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        lock (_gate) _running[requestId] = cts;
        _ = ExecuteAsync(service, result.Job, cts);
        return Task.FromResult(result.Message + " — execution started");
    }

    public Task<string> CancelAsync(string caller, string requestId)
    {
        var service = Service();
        if (service is null) return Task.FromResult("no active project");
        var result = service.Cancel(caller, requestId, DateTimeOffset.UtcNow);
        if (result.Success)
        {
            lock (_gate)
                if (_running.TryGetValue(requestId, out var cts)) cts.Cancel();
        }
        return Task.FromResult(result.Message);
    }

    public Task<string> StatusAsync(string? requestId, string? environment)
    {
        var service = Service();
        if (service is null) return Task.FromResult("no active project");
        var jobs = requestId is null ? service.ReadAll() : new[] { service.Read(requestId) }.OfType<BrowserJob>().ToList();
        if (!string.IsNullOrWhiteSpace(environment))
            jobs = jobs.Where(j => j.EnvironmentId.Equals(environment, StringComparison.OrdinalIgnoreCase)).ToList();
        if (jobs.Count == 0) return Task.FromResult("no browser requests");
        return Task.FromResult(string.Join('\n', jobs.Take(30).Select(j =>
            $"{j.Id} — {j.EnvironmentId}/{j.Mode.ToString().ToLowerInvariant()} — {j.Status.ToString().ToLowerInvariant()} — requester {j.Requester}")));
    }

    public Task<string> ArtifactsAsync(string caller, string requestId)
    {
        var service = Service();
        if (service is null) return Task.FromResult("no active project");
        var job = service.Read(requestId);
        if (job is null) return Task.FromResult("unknown browser request");
        var registry = Styloagent.Core.Environments.EnvironmentOwnershipStore.Read(_vm.EnvironmentsRootOrNull!);
        var environment = registry.Environments.FirstOrDefault(e => e.Definition.Id == job.EnvironmentId);
        if (caller != job.Requester && caller != environment?.Owner && caller != registry.ControlOwner)
            return Task.FromResult("denied: artifacts are visible only to requester or environment authority");
        if (job.Status != BrowserJobStatus.Completed || job.ArtifactPath is null)
            return Task.FromResult($"no artifact: browser request is {job.Status.ToString().ToLowerInvariant()}");
        return Task.FromResult(job.ArtifactPath);
    }

    public async Task RevokeEnvironmentAsync(string caller, string environment)
    {
        var service = Service();
        if (service is null) return;
        foreach (var job in service.ReadAll().Where(j =>
                     j.EnvironmentId.Equals(environment, StringComparison.OrdinalIgnoreCase) &&
                     j.Status is BrowserJobStatus.Pending or BrowserJobStatus.Approved or BrowserJobStatus.Running))
            await CancelAsync(caller, job.Id).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(BrowserJobService service, BrowserJob approved, CancellationTokenSource cts)
    {
        try
        {
            var running = service.MarkRunning(approved.Id, DateTimeOffset.UtcNow);
            if (!running.Success || running.Job is null) return;
            var runner = new PlaywrightBrowserRunner(_vm.EnvironmentsRootOrNull!, _vm.BrowserRootOrNull!);
            var result = await runner.RunAsync(running.Job, cts.Token).ConfigureAwait(false);
            // A user cancellation wins over a late runner completion.
            if (service.Read(approved.Id)?.Status == BrowserJobStatus.Running)
                service.Complete(approved.Id, result, DateTimeOffset.UtcNow);
        }
        finally
        {
            lock (_gate) _running.Remove(approved.Id);
            cts.Dispose();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _vm.Router?.Refresh());
        }
    }

    private BrowserJobService? Service()
    {
        if (_vm.EnvironmentsRootOrNull is not { } environments || _vm.BrowserRootOrNull is not { } browser)
            return null;
        var key = environments + "\n" + browser;
        lock (_gate)
        {
            if (_service is not null && _serviceKey == key) return _service;
            _service = new BrowserJobService(environments, browser);
            _service.ReconcileInterrupted(DateTimeOffset.UtcNow);
            _serviceKey = key;
            return _service;
        }
    }
}
