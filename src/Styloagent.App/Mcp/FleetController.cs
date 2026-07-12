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
        => Dispatcher.UIThread.InvokeAsync(() => _vm.SpawnChild(req)).GetTask();

    public FleetSnapshot Snapshot()
        => Dispatcher.UIThread.CheckAccess()
            ? _vm.BuildFleetSnapshot()
            : Dispatcher.UIThread.InvokeAsync(_vm.BuildFleetSnapshot).GetTask().GetAwaiter().GetResult();

    public Task<IssueOutcome> ReportIssueAsync(IssueRequest req)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.ReportIssue(req)).GetTask();

    public Task<WrapUpOutcome> WrapUpAsync(string callerPrefix)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.WrapUp(callerPrefix)).GetTask();

    public Task<MessageOutcome> SendMessageAsync(MessageRequest req)
        => Dispatcher.UIThread.InvokeAsync(() => _vm.SendBusMessage(req)).GetTask();

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
}
