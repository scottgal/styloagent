using Styloagent.Core.Environments;

namespace Styloagent.Core.Browser;

/// <summary>
/// Deterministic browser request/approval lifecycle. Approval atomically acquires the environment's
/// read or write browser capacity; execution is delegated to an app-layer runner.
/// </summary>
public sealed class BrowserJobService
{
    private static readonly object Gate = new();
    private readonly string _environmentsRoot;
    private readonly BrowserJobStore _store;

    public BrowserJobService(string environmentsRoot, string browserRoot)
        => (_environmentsRoot, _store) = (environmentsRoot, new BrowserJobStore(browserRoot));

    public BrowserOperationResult Request(string caller, string environmentId, string mode, string purpose,
        string relativePath, string? selector, bool fullPage, string? credentialRef, DateTimeOffset now)
    {
        lock (Gate)
        {
            var environment = FindEnvironment(environmentId);
            if (environment is null) return BrowserOperationResult.Fail($"unknown environment '{environmentId}'");
            if (!Enum.TryParse<BrowserRunMode>(mode, true, out var parsedMode))
                return BrowserOperationResult.Fail("mode must be observe, test, or operate");
            if (string.IsNullOrWhiteSpace(purpose)) return BrowserOperationResult.Fail("purpose is required");
            if (ContainsCredentialMaterial(purpose) || ContainsCredentialMaterial(relativePath) ||
                ContainsCredentialMaterial(selector))
                return BrowserOperationResult.Fail("request text appears to contain credential material; use credential_ref only");
            if (environment.Definition.Targets.WebOrigin is null)
                return BrowserOperationResult.Fail($"environment '{environmentId}' has no webOrigin");
            if (!SafeRelativePath(relativePath))
                return BrowserOperationResult.Fail("relative_path must be a same-origin path beginning with '/'");
            if (environment.Definition.Classification == "production" && parsedMode != BrowserRunMode.Observe)
                return BrowserOperationResult.Fail("production mutation requires an operator approval capability that is not configured");
            if (!ValidCredentialReference(credentialRef))
                return BrowserOperationResult.Fail("credential_ref must be an opaque keychain://, infisical://, or secret:// reference");
            if (!string.IsNullOrWhiteSpace(credentialRef) &&
                !string.Equals(credentialRef.Trim(), environment.Definition.Targets.BrowserCredentialRef, StringComparison.Ordinal))
                return BrowserOperationResult.Fail("credential_ref is not approved for this environment");
            var job = _store.Create(caller, environment.Definition.Id, parsedMode, purpose.Trim(), relativePath,
                string.IsNullOrWhiteSpace(selector) ? null : selector.Trim(), fullPage,
                string.IsNullOrWhiteSpace(credentialRef) ? null : credentialRef.Trim(), now);
            return BrowserOperationResult.Ok($"browser request {job.Id} is pending approval by {environment.Owner}", job);
        }
    }

    public BrowserOperationResult Approve(string caller, string id, DateTimeOffset now)
    {
        lock (Gate)
        {
            var job = _store.Read(id);
            if (job is null) return BrowserOperationResult.Fail("unknown browser request");
            if (job.Status != BrowserJobStatus.Pending)
                return BrowserOperationResult.Fail($"browser request is {job.Status.ToString().ToLowerInvariant()}", job);
            var registry = EnvironmentOwnershipStore.Read(_environmentsRoot);
            var environment = registry.Environments.FirstOrDefault(e => e.Definition.Id == job.EnvironmentId);
            if (environment is null) return BrowserOperationResult.Fail("environment no longer exists", job);
            if (caller != environment.Owner && caller != registry.ControlOwner)
                return BrowserOperationResult.Fail($"denied: {environment.Owner} owns {environment.Definition.DisplayName}", job);
            var active = _store.ReadAll().Where(j => j.EnvironmentId == job.EnvironmentId &&
                j.Status is BrowserJobStatus.Approved or BrowserJobStatus.Running).ToList();
            var activeReads = active.Count(j => j.Mode == BrowserRunMode.Observe);
            var activeWrites = active.Count(j => j.Mode != BrowserRunMode.Observe);
            if (job.Mode == BrowserRunMode.Observe && activeWrites > 0)
                return BrowserOperationResult.Fail("queued: an environment-mutating browser run is active", job);
            if (job.Mode != BrowserRunMode.Observe && activeReads > 0)
                return BrowserOperationResult.Fail("queued: read-only browser runs must finish before mutation starts", job);
            var used = job.Mode == BrowserRunMode.Observe
                ? activeReads
                : activeWrites;
            var capacity = job.Mode == BrowserRunMode.Observe
                ? environment.Definition.Capacity.BrowserRead
                : environment.Definition.Capacity.BrowserWrite;
            if (used >= capacity)
                return BrowserOperationResult.Fail($"queued: browser {job.Mode.ToString().ToLowerInvariant()} capacity {used}/{capacity} is full", job);
            var approved = job with { Status = BrowserJobStatus.Approved, Approver = caller, UpdatedAt = now.ToUniversalTime() };
            _store.Write(approved);
            return BrowserOperationResult.Ok($"approved browser request {id}", approved);
        }
    }

    public BrowserOperationResult MarkRunning(string id, DateTimeOffset now)
        => Transition(id, BrowserJobStatus.Approved, job => job with
        { Status = BrowserJobStatus.Running, UpdatedAt = now.ToUniversalTime() });

    public BrowserOperationResult Complete(string id, BrowserRunResult result, DateTimeOffset now)
        => Transition(id, BrowserJobStatus.Running, job => job with
        {
            Status = result.Success ? BrowserJobStatus.Completed : BrowserJobStatus.Failed,
            ArtifactPath = result.ArtifactPath,
            Failure = result.Failure,
            UpdatedAt = now.ToUniversalTime(),
        });

    public BrowserOperationResult Cancel(string caller, string id, DateTimeOffset now)
    {
        lock (Gate)
        {
            var job = _store.Read(id);
            if (job is null) return BrowserOperationResult.Fail("unknown browser request");
            var registry = EnvironmentOwnershipStore.Read(_environmentsRoot);
            var environment = registry.Environments.FirstOrDefault(e => e.Definition.Id == job.EnvironmentId);
            var authorized = caller == job.Requester || caller == registry.ControlOwner || caller == environment?.Owner;
            if (!authorized) return BrowserOperationResult.Fail("denied: only requester or environment owner may cancel", job);
            if (job.Status is BrowserJobStatus.Completed or BrowserJobStatus.Failed or BrowserJobStatus.Cancelled)
                return BrowserOperationResult.Fail($"browser request is already {job.Status.ToString().ToLowerInvariant()}", job);
            var cancelled = job with { Status = BrowserJobStatus.Cancelled, UpdatedAt = now.ToUniversalTime() };
            _store.Write(cancelled);
            return BrowserOperationResult.Ok($"cancelled browser request {id}", cancelled);
        }
    }

    public BrowserJob? Read(string id) => _store.Read(id);
    public IReadOnlyList<BrowserJob> ReadAll() => _store.ReadAll();

    /// <summary>Fails reservations left active by a previous broker process so capacity cannot remain stuck.</summary>
    public void ReconcileInterrupted(DateTimeOffset now)
    {
        lock (Gate)
        {
            foreach (var job in _store.ReadAll().Where(j =>
                         j.Status is BrowserJobStatus.Approved or BrowserJobStatus.Running))
                _store.Write(job with
                {
                    Status = BrowserJobStatus.Failed,
                    Failure = "browser broker restarted before completion",
                    UpdatedAt = now.ToUniversalTime(),
                });
        }
    }

    private BrowserOperationResult Transition(string id, BrowserJobStatus expected, Func<BrowserJob, BrowserJob> change)
    {
        lock (Gate)
        {
            var job = _store.Read(id);
            if (job is null) return BrowserOperationResult.Fail("unknown browser request");
            if (job.Status != expected) return BrowserOperationResult.Fail($"expected {expected}, found {job.Status}", job);
            var changed = change(job);
            _store.Write(changed);
            return BrowserOperationResult.Ok($"browser request {id} is {changed.Status.ToString().ToLowerInvariant()}", changed);
        }
    }

    private EnvironmentState? FindEnvironment(string id)
    {
        var normalized = EnvironmentRegistry.NormalizeId(id);
        return normalized is null ? null : EnvironmentOwnershipStore.Read(_environmentsRoot).Environments
            .FirstOrDefault(e => e.Definition.Id == normalized);
    }

    private static bool SafeRelativePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith('/') && Uri.TryCreate(path, UriKind.Relative, out _) && !path.StartsWith("//", StringComparison.Ordinal);

    private static bool ValidCredentialReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme is "keychain" or "infisical" or "secret" &&
               string.IsNullOrEmpty(uri.UserInfo) && value.Length <= 256;
    }

    private static bool ContainsCredentialMaterial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("sk-", StringComparison.OrdinalIgnoreCase);
    }
}
