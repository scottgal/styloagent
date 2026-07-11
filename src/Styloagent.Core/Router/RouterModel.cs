namespace Styloagent.Core.Router;

/// <summary>An SSH account (capacity 1, lockout-tracked) or a test slot pool (capacity N).</summary>
public enum ResourceKind { Account, Slot }

/// <summary>A pending claim request (drop-once markdown file).</summary>
public sealed record Claim(string Prefix, DateTimeOffset Timestamp, string Purpose);

/// <summary>An active grant/lease. <see cref="HeartbeatAt"/> is the grant file's mtime.</summary>
public sealed record Grant(string Prefix, DateTimeOffset GrantedAt, DateTimeOffset HeartbeatAt, DateTimeOffset ClaimTimestamp);

/// <summary>One logged SSH auth attempt.</summary>
public sealed record AttemptLine(DateTimeOffset Timestamp, bool Ok);

/// <summary>The full state of one resource (an account or a slot pool) as read from the ledger.</summary>
public sealed record ResourceState(
    string Env,
    ResourceKind Kind,
    string Name,
    ResourcePolicy Policy,
    IReadOnlyList<Claim> Claims,
    IReadOnlyList<Grant> Grants,
    IReadOnlyList<AttemptLine> Attempts);

/// <summary>All resources across all environments in the ledger.</summary>
public sealed record RouterState(IReadOnlyList<ResourceState> Resources);
