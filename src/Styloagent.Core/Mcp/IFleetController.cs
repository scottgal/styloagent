using Styloagent.Core.Git;

namespace Styloagent.Core.Mcp;

/// <summary>
/// The seam the MCP tools call. Implemented in the App (marshals to the UI thread and drives the
/// roster); faked in tests. Keeps the tool layer app-agnostic.
/// </summary>
public interface IFleetController
{
    Task<SpawnOutcome> SpawnAsync(SpawnRequest req);
    FleetSnapshot Snapshot();
    Task<IssueOutcome> ReportIssueAsync(IssueRequest req);
    Task<WrapUpOutcome> WrapUpAsync(string callerPrefix);
    Task<MessageOutcome> SendMessageAsync(MessageRequest req);
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
    IReadOnlyList<RepoInfo> ListRepos();
}
