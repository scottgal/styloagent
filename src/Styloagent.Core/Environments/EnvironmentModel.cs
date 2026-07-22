namespace Styloagent.Core.Environments;

/// <summary>A configured external environment whose authority and shared resources are routed by Styloagent.</summary>
public sealed record EnvironmentDefinition(
    string Id,
    string DisplayName,
    string Description,
    string Color,
    string Icon,
    string Classification,
    string ConfiguredOwner,
    string FallbackOwner,
    string Status,
    EnvironmentTargets Targets,
    EnvironmentCapacity Capacity);

/// <summary>Non-secret connection metadata. Credential values never belong in this model.</summary>
public sealed record EnvironmentTargets(
    string? WebOrigin,
    string? ApiOrigin,
    string? SshHost,
    string? SshAccount,
    string? CredentialRef,
    string? BrowserCredentialRef);

/// <summary>Deterministic concurrency limits advertised by an environment.</summary>
public sealed record EnvironmentCapacity(int BrowserRead, int BrowserWrite, int Ssh, int Deploy);

public enum EnvironmentOwnershipAction { Assign, Offer, Accept, Revoke, Return }

/// <summary>One immutable authority transition from the append-only environment ownership journal.</summary>
public sealed record EnvironmentOwnershipEvent(
    string Id,
    string EnvironmentId,
    EnvironmentOwnershipAction Action,
    string Initiator,
    string? FromOwner,
    string? ToOwner,
    string Reason,
    bool Force,
    DateTimeOffset Timestamp);

/// <summary>The effective environment authority after projecting its definition and ownership journal.</summary>
public sealed record EnvironmentState(
    EnvironmentDefinition Definition,
    string Owner,
    string? PendingOwner,
    IReadOnlyList<EnvironmentOwnershipEvent> History);

public sealed record EnvironmentRegistryState(string ControlOwner, IReadOnlyList<EnvironmentState> Environments);

public sealed record EnvironmentOperationResult(bool Success, string Message)
{
    public static EnvironmentOperationResult Ok(string message) => new(true, message);
    public static EnvironmentOperationResult Fail(string message) => new(false, message);
}
