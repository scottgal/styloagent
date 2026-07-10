namespace Styloagent.Core.Mcp;

public enum RejectReason { FleetFull, MaxDepth, Paused, DuplicatePrefix, InvalidPrefix, UnknownParent }

/// <summary>One live agent as the governor / list_fleet sees it.</summary>
public sealed record FleetMember(string Prefix, string Responsibility, string? ParentPrefix, int Depth, string State);

/// <summary>The fleet + its policy, handed to the pure governor.</summary>
public sealed record FleetState(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>What list_fleet returns to an agent.</summary>
public sealed record FleetSnapshot(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>A spawn_agent request, parented by prefix.</summary>
public sealed record SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt);

/// <summary>Result of a spawn attempt (never an exception).</summary>
public sealed record SpawnOutcome(bool Spawned, string? Prefix, RejectReason? Reason, string Message)
{
    public static SpawnOutcome Ok(string prefix) => new(true, prefix, null, $"spawned {prefix}");
    public static SpawnOutcome Reject(RejectReason reason, string message) => new(false, null, reason, message);
}

/// <summary>Governor verdict.</summary>
public sealed record Decision(bool Allowed, RejectReason? Reason, string Message)
{
    public static Decision Allow() => new(true, null, "allowed");
    public static Decision Deny(RejectReason reason, string message) => new(false, reason, message);
}
