namespace Styloagent.Core.Mcp;

public enum RejectReason { FleetFull, MaxDepth, Paused, DuplicatePrefix, InvalidPrefix, UnknownParent }

/// <summary>One live agent as the governor / list_fleet sees it.</summary>
public sealed record FleetMember(string Prefix, string Responsibility, string? ParentPrefix, int Depth, string State);

/// <summary>The fleet + its policy, handed to the pure governor.</summary>
public sealed record FleetState(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>What list_fleet returns to an agent.</summary>
public sealed record FleetSnapshot(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>A spawn_agent request, parented by prefix.</summary>
public sealed record SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt, bool Worktree);

/// <summary>Result of a spawn attempt (never an exception).</summary>
public sealed record SpawnOutcome(bool Spawned, string? Prefix, RejectReason? Reason, string Message)
{
    public static SpawnOutcome Ok(string prefix) => new(true, prefix, null, $"spawned {prefix}");
    public static SpawnOutcome Reject(RejectReason reason, string message) => new(false, null, reason, message);
}

/// <summary>A report_issue request from an agent (reporter is the caller prefix).</summary>
public sealed record IssueRequest(string Reporter, string Title, string Detail, string Severity);

/// <summary>Result of filing an issue (never an exception).</summary>
public sealed record IssueOutcome(bool Filed, string? Id, string Message)
{
    public static IssueOutcome Ok(string id) => new(true, id, $"filed {id}");
    public static IssueOutcome Fail(string message) => new(false, null, message);
}

/// <summary>A send_message request from an agent (sender is the caller prefix).</summary>
public sealed record MessageRequest(string From, string To, string Subject, string Body, string Priority);

/// <summary>Result of sending a bus message (never an exception).</summary>
public sealed record MessageOutcome(bool Sent, string? Path, string Message)
{
    public static MessageOutcome Ok(string path) => new(true, path, $"sent → {path}");
    public static MessageOutcome Fail(string message) => new(false, null, message);
}

/// <summary>Governor verdict.</summary>
public sealed record Decision(bool Allowed, RejectReason? Reason, string Message)
{
    public static Decision Allow() => new(true, null, "allowed");
    public static Decision Deny(RejectReason reason, string message) => new(false, reason, message);
}
