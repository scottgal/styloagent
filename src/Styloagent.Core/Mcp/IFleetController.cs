using Styloagent.Core.Architecture;
using Styloagent.Core.Git;

namespace Styloagent.Core.Mcp;

/// <summary>
/// The seam the MCP tools call. Implemented in the App (marshals to the UI thread and drives the
/// roster); faked in tests. Keeps the tool layer app-agnostic.
/// </summary>
public interface IFleetController
{
    Task<SpawnOutcome> SpawnAsync(SpawnRequest req);
    Task<string> RenameAgentAsync(string prefix, string name);
    FleetSnapshot Snapshot();
    AgentCapabilities AgentCapabilities();
    Projects.ModelPolicy ModelPolicy();
    Task<IssueOutcome> ReportIssueAsync(IssueRequest req);
    Task<WrapUpOutcome> WrapUpAsync(string callerPrefix);
    Task<MessageOutcome> SendMessageAsync(MessageRequest req);
    Task<MessageOutcome> ReplyToThreadAsync(string callerPrefix, string thread, string body);
    Task<string> CaptureScreenshotAsync(string? target);

    // Fleet-control surface for an orchestrator agent.
    FleetStatusReport FleetStatus();
    IReadOnlyList<TimelineOp> ReadTimeline(int limit);
    Task<string> DehydrateAgentAsync(string prefix);
    Task<string> RehydrateAgentAsync(string prefix);
    Task<string> ReadAgentAsync(string prefix);
    string WhoTouched(string path);
    IReadOnlyList<string> RecentFiles(int limit);
    IReadOnlyList<Docs.DocSearchHit> SearchDocs(string query, int limit);
    Task<Memory.MemoryRecallResult> RecallMemoryAsync(string query, string? type, int limit, int maxBytes);
    Task<Retrieval.ContextRetrievalResult> RetrieveContextAsync(string caller, string query, string[]? sources, int limit, int maxBytes);
    IReadOnlyList<RepoInfo> ListRepos();
    IReadOnlyList<AuthorityViolation> LintAuthority();
}
