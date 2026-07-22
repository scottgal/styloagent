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

    /// <summary>Create a displayable environment definition. Control-plane owner only.</summary>
    Task<string> RegisterEnvironmentAsync(string caller, string id, string displayName, string classification);
    Task<string> ConfigureBrowserEnvironmentAsync(string caller, string environment, string webOrigin,
        string? browserCredentialRef, int readCapacity, int writeCapacity);

    /// <summary>Immediately assign an environment to an agent. Control-plane owner only.</summary>
    Task<string> AssignEnvironmentAsync(string caller, string environment, string owner, string reason);

    /// <summary>Offer an environment handoff; the recipient must accept it.</summary>
    Task<string> OfferEnvironmentAsync(string caller, string environment, string owner, string reason);

    /// <summary>Accept a pending environment handoff addressed to the caller.</summary>
    Task<string> AcceptEnvironmentAsync(string caller, string environment);

    /// <summary>Return an owned environment to its configured fallback owner.</summary>
    Task<string> ReturnEnvironmentAsync(string caller, string environment, string reason);

    /// <summary>Revoke delegated ownership. Control-plane owner only.</summary>
    Task<string> RevokeEnvironmentAsync(string caller, string environment, string reason, bool force);

    /// <summary>Return the environment registry and effective delegated ownership.</summary>
    Task<string> EnvironmentStatusAsync(string? environment);
}
