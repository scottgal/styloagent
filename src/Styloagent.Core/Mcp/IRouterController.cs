namespace Styloagent.Core.Mcp;

/// <summary>
/// The seam the MCP router tools call. Implemented in the App (reads/writes the markdown ledger);
/// faked in tests. Keeps the tool layer app-agnostic.
/// </summary>
public interface IRouterController
{
    /// <summary>Drop a claim for <paramref name="resource"/> and return a disposition string.</summary>
    Task<string> ClaimAsync(string caller, string env, string resource, string purpose);

    /// <summary>Touch the heartbeat file for an active grant. Returns "ok" or "no active grant".</summary>
    Task<string> HeartbeatAsync(string caller, string env, string resource);

    /// <summary>Release a hold or pending claim for <paramref name="resource"/>.</summary>
    Task<string> ReleaseAsync(string caller, string env, string resource);

    /// <summary>Append an attempt record for an account resource.</summary>
    Task<string> LogAttemptAsync(string caller, string env, string account, bool ok);

    /// <summary>Return a human/agent-readable snapshot of current holders, queues and cooldowns.</summary>
    Task<string> StatusAsync(string? env);
}
