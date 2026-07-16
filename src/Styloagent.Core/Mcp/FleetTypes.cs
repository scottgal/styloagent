namespace Styloagent.Core.Mcp;

public enum RejectReason { FleetFull, MaxDepth, Paused, DuplicatePrefix, InvalidPrefix, UnknownParent }

/// <summary>One live agent as the governor / list_fleet sees it.</summary>
public sealed record FleetMember(string Prefix, string Responsibility, string? ParentPrefix, int Depth, string State);

/// <summary>The fleet + its policy, handed to the pure governor.</summary>
public sealed record FleetState(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>What list_fleet returns to an agent.</summary>
public sealed record FleetSnapshot(IReadOnlyList<FleetMember> Members, int MaxFleet, int MaxDepth, bool Paused);

/// <summary>
/// A spawn_agent request, parented by prefix. <paramref name="MissionDoc"/> is an optional larger mission
/// brief: when non-empty, it's placed at <c>.styloagent/missions/&lt;prefix&gt;.md</c> in the new agent's tree
/// (committed on its branch when isolated) so a worktree agent can read it from its own checkout.
/// </summary>
public sealed record SpawnRequest(string ParentPrefix, string Prefix, string Responsibility, string Dir, string LaunchPrompt, bool Worktree, string MissionDoc = "");

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

/// <summary>Rich, live status of one agent — what an orchestrator needs to run the fleet.</summary>
/// <param name="State">Hook state: working | idle | needs-you | exited | unknown.</param>
/// <param name="Activity">What it's doing right now (e.g. "editing", "running commands", "idle").</param>
/// <param name="IdleSeconds">Seconds since its last output (-1 if it has produced none yet).</param>
/// <param name="Usage">Context readout, e.g. "83k · 22%" (empty until known).</param>
public sealed record AgentStatus(
    string Prefix, string Responsibility, string State, string Activity,
    int IdleSeconds, string Usage, bool Worktree, string Repo = "");

/// <summary>A whole-fleet situational snapshot for the fleet_status tool.</summary>
public sealed record FleetStatusReport(
    IReadOnlyList<AgentStatus> Agents, int Working, int Waiting, bool Paused);

/// <summary>One recorded operation for the read_timeline tool.</summary>
public sealed record TimelineOp(string Time, string Agent, string What);

/// <summary>One repo in the open workspace, for the list_repos tool.</summary>
/// <param name="Prefix">The repo overview's channel prefix (e.g. "overview-" for the primary, "lucidresume-").</param>
/// <param name="Primary">True for the workspace's primary (anchor) repo.</param>
public sealed record RepoInfo(
    string Name, string Path, int Index, string Prefix, string ColorHex, bool Primary);

/// <summary>Governor verdict.</summary>
public sealed record Decision(bool Allowed, RejectReason? Reason, string Message)
{
    public static Decision Allow() => new(true, null, "allowed");
    public static Decision Deny(RejectReason reason, string message) => new(false, reason, message);
}
