using Avalonia.Threading;
using Styloagent.App.ViewModels;
using Styloagent.Core.Git;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Mcp;

/// <summary>Bridges the MCP tools to the cockpit VM, marshalling every call to the UI thread.</summary>
public sealed class FleetController : IFleetController
{
    private readonly MainWindowViewModel _vm;

    public FleetController(MainWindowViewModel vm) => _vm = vm;

    public Task<SpawnOutcome> SpawnAsync(SpawnRequest req)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.SpawnChildAsync(req));

    public Task<string> RenameAgentAsync(string prefix, string name)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.RenameAgentAsync(prefix, name));

    public FleetSnapshot Snapshot()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.BuildFleetSnapshot()
            : Dispatcher.UIThread.InvokeAsync(_vm.BuildFleetSnapshot).GetTask().GetAwaiter().GetResult();

    public AgentCapabilities AgentCapabilities()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.BuildAgentCapabilities()
            : Dispatcher.UIThread.InvokeAsync(_vm.BuildAgentCapabilities).GetTask().GetAwaiter().GetResult();

    public Styloagent.Core.Projects.ModelPolicy ModelPolicy()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.BuildModelPolicy()
            : Dispatcher.UIThread.InvokeAsync(_vm.BuildModelPolicy).GetTask().GetAwaiter().GetResult();

    public Task<IssueOutcome> ReportIssueAsync(IssueRequest req)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.ReportIssue(req)).GetTask();

    public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.WrapUpAsync(callerPrefix));

    public Task<MessageOutcome> SendMessageAsync(MessageRequest req)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.SendBusMessage(req));

    public Task<MessageOutcome> ReplyToThreadAsync(string callerPrefix, string thread, string body)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.ReplyToBusThreadAsync(callerPrefix, thread, body));

    public Task<string> CaptureScreenshotAsync(string? target)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.CaptureScreenshotToFileAsync(target));

    public FleetStatusReport FleetStatus()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.BuildFleetStatus()
            : Dispatcher.UIThread.InvokeAsync(_vm.BuildFleetStatus).GetTask().GetAwaiter().GetResult();

    public IReadOnlyList<TimelineOp> ReadTimeline(int limit)
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.ReadTimeline(limit)
            : Dispatcher.UIThread.InvokeAsync(() => _vm.ReadTimeline(limit)).GetTask().GetAwaiter().GetResult();

    public Task<string> DehydrateAgentAsync(string prefix)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.DehydrateAgentByPrefixAsync(prefix));

    public Task<string> RehydrateAgentAsync(string prefix)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.RehydrateAgentByPrefixAsync(prefix));

    public Task<string> ReadAgentAsync(string prefix)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.ReadAgentOutput(prefix));

    public string WhoTouched(string path)
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.WhoTouched(path)
            : Dispatcher.UIThread.InvokeAsync(() => _vm.WhoTouched(path)).GetTask().GetAwaiter().GetResult();

    public IReadOnlyList<string> RecentFiles(int limit)
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.RecentFiles(limit)
            : Dispatcher.UIThread.InvokeAsync(() => _vm.RecentFiles(limit)).GetTask().GetAwaiter().GetResult();

    public IReadOnlyList<Styloagent.Core.Docs.DocSearchHit> SearchDocs(string query, int limit)
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.SearchDocs(query, limit)
            : Dispatcher.UIThread.InvokeAsync(() => _vm.SearchDocs(query, limit)).GetTask().GetAwaiter().GetResult();

    public Task<Styloagent.Core.Memory.MemoryRecallResult> RecallMemoryAsync(string query, string? type, int limit, int maxBytes)
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.RecallMemoryAsync(query, type, limit, maxBytes)
            : Dispatcher.UIThread.InvokeAsync(() => _vm.RecallMemoryAsync(query, type, limit, maxBytes));

    public Task<Styloagent.Core.Retrieval.ContextRetrievalResult> RetrieveContextAsync(string caller, string query, string[]? sources, int limit, int maxBytes)
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.RetrieveContextAsync(caller, query, sources, limit, maxBytes)
            : Dispatcher.UIThread.InvokeAsync(() => _vm.RetrieveContextAsync(caller, query, sources, limit, maxBytes));

    public IReadOnlyList<RepoInfo> ListRepos()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.BuildRepoList()
            : Dispatcher.UIThread.InvokeAsync(_vm.BuildRepoList).GetTask().GetAwaiter().GetResult();

    public IReadOnlyList<Styloagent.Core.Architecture.AuthorityViolation> LintAuthority()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.LintAuthority()
            : Dispatcher.UIThread.InvokeAsync(_vm.LintAuthority).GetTask().GetAwaiter().GetResult();
}
