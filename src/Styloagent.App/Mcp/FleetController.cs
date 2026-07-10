using Avalonia.Threading;
using Styloagent.App.ViewModels;
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
}
