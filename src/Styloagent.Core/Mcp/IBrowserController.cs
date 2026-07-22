namespace Styloagent.Core.Mcp;

/// <summary>Application seam for governed Playwright requests and execution.</summary>
public interface IBrowserController
{
    Task<string> RequestAsync(string caller, string environment, string mode, string purpose,
        string relativePath, string? selector, bool fullPage, string? credentialRef);
    Task<string> ApproveAsync(string caller, string requestId);
    Task<string> CancelAsync(string caller, string requestId);
    Task<string> StatusAsync(string? requestId, string? environment);
    Task<string> ArtifactsAsync(string caller, string requestId);
    /// <summary>Internal control-plane operation used by force-revoke to terminate environment work.</summary>
    Task RevokeEnvironmentAsync(string caller, string environment);
}
