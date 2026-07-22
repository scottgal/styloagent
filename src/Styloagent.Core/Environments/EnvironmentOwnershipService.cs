namespace Styloagent.Core.Environments;

/// <summary>Authorization and transition rules for delegated environment ownership.</summary>
public sealed class EnvironmentOwnershipService
{
    private static readonly object MutationGate = new();
    private readonly string _root;
    public EnvironmentOwnershipService(string root) => _root = root;

    public EnvironmentOperationResult Assign(string caller, string environmentId, string newOwner, string reason, DateTimeOffset now)
    {
        lock (MutationGate) return AssignCore(caller, environmentId, newOwner, reason, now);
    }

    private EnvironmentOperationResult AssignCore(string caller, string environmentId, string newOwner, string reason, DateTimeOffset now)
    {
        var found = Find(environmentId);
        if (found.Error is not null) return found.Error;
        if (caller != found.Registry!.ControlOwner) return Denied(found.Registry.ControlOwner);
        var owner = EnvironmentRegistry.NormalizeOwner(newOwner, found.Registry.ControlOwner);
        EnvironmentOwnershipStore.Append(_root, found.State!.Definition.Id, EnvironmentOwnershipAction.Assign,
            caller, found.State.Owner, owner, reason, false, now);
        return EnvironmentOperationResult.Ok($"{owner} now owns {found.State.Definition.DisplayName}");
    }

    public EnvironmentOperationResult Offer(string caller, string environmentId, string newOwner, string reason, DateTimeOffset now)
    {
        lock (MutationGate) return OfferCore(caller, environmentId, newOwner, reason, now);
    }

    private EnvironmentOperationResult OfferCore(string caller, string environmentId, string newOwner, string reason, DateTimeOffset now)
    {
        var found = Find(environmentId);
        if (found.Error is not null) return found.Error;
        var state = found.State!;
        if (caller != found.Registry!.ControlOwner && caller != state.Owner)
            return EnvironmentOperationResult.Fail($"denied: only {state.Owner} or {found.Registry.ControlOwner} may offer this environment");
        var owner = EnvironmentRegistry.NormalizeOwner(newOwner, found.Registry.ControlOwner);
        EnvironmentOwnershipStore.Append(_root, state.Definition.Id, EnvironmentOwnershipAction.Offer,
            caller, state.Owner, owner, reason, false, now);
        return EnvironmentOperationResult.Ok($"ownership of {state.Definition.DisplayName} offered to {owner}");
    }

    public EnvironmentOperationResult Accept(string caller, string environmentId, DateTimeOffset now)
    {
        lock (MutationGate) return AcceptCore(caller, environmentId, now);
    }

    private EnvironmentOperationResult AcceptCore(string caller, string environmentId, DateTimeOffset now)
    {
        var found = Find(environmentId);
        if (found.Error is not null) return found.Error;
        if (found.State!.PendingOwner != caller)
            return EnvironmentOperationResult.Fail($"denied: ownership is offered to {found.State.PendingOwner ?? "nobody"}");
        EnvironmentOwnershipStore.Append(_root, found.State.Definition.Id, EnvironmentOwnershipAction.Accept,
            caller, found.State.Owner, caller, "accepted ownership", false, now);
        return EnvironmentOperationResult.Ok($"{caller} now owns {found.State.Definition.DisplayName}");
    }

    public EnvironmentOperationResult Revoke(string caller, string environmentId, string reason, bool force, DateTimeOffset now)
    {
        lock (MutationGate) return RevokeCore(caller, environmentId, reason, force, now);
    }

    private EnvironmentOperationResult RevokeCore(string caller, string environmentId, string reason, bool force, DateTimeOffset now)
    {
        var found = Find(environmentId);
        if (found.Error is not null) return found.Error;
        if (caller != found.Registry!.ControlOwner) return Denied(found.Registry.ControlOwner);
        var fallback = found.State!.Definition.FallbackOwner;
        EnvironmentOwnershipStore.Append(_root, found.State.Definition.Id, EnvironmentOwnershipAction.Revoke,
            caller, found.State.Owner, fallback, reason, force, now);
        var suffix = force ? "; active access must be terminated" : "; existing leases may drain";
        return EnvironmentOperationResult.Ok($"ownership revoked from {found.State.Owner}; {fallback} now owns {found.State.Definition.DisplayName}{suffix}");
    }

    public EnvironmentOperationResult Return(string caller, string environmentId, string reason, DateTimeOffset now)
    {
        lock (MutationGate) return ReturnCore(caller, environmentId, reason, now);
    }

    private EnvironmentOperationResult ReturnCore(string caller, string environmentId, string reason, DateTimeOffset now)
    {
        var found = Find(environmentId);
        if (found.Error is not null) return found.Error;
        if (caller != found.State!.Owner) return EnvironmentOperationResult.Fail($"denied: {found.State.Owner} owns this environment");
        var fallback = found.State.Definition.FallbackOwner;
        EnvironmentOwnershipStore.Append(_root, found.State.Definition.Id, EnvironmentOwnershipAction.Return,
            caller, found.State.Owner, fallback, reason, false, now);
        return EnvironmentOperationResult.Ok($"{caller} returned {found.State.Definition.DisplayName} to {fallback}");
    }

    private (EnvironmentRegistryState? Registry, EnvironmentState? State, EnvironmentOperationResult? Error) Find(string id)
    {
        var registry = EnvironmentOwnershipStore.Read(_root);
        var normalized = EnvironmentRegistry.NormalizeId(id);
        var state = normalized is null ? null : registry.Environments.FirstOrDefault(e => e.Definition.Id == normalized);
        return state is null
            ? (registry, null, EnvironmentOperationResult.Fail($"unknown environment '{id}'"))
            : (registry, state, null);
    }

    private static EnvironmentOperationResult Denied(string controlOwner)
        => EnvironmentOperationResult.Fail($"denied: {controlOwner} owns the environment control plane");
}
