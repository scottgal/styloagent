using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Dock;

/// <summary>
/// Dock factory that builds the Styloagent shell layout:
/// [ ToolDock(Left) | ProportionalDockSplitter | DocumentDock(center) | ProportionalDockSplitter | ToolDock(Right) ]
/// </summary>
public sealed class StyloagentDockFactory : Factory
{
    private readonly AgentPaneViewModel? _agentPane;
    private readonly BusViewModel? _busViewModel;

    /// <summary>The center DocumentDock; populated after <see cref="CreateLayout"/> is called.</summary>
    public DocumentDock? DocumentDock { get; private set; }

    /// <summary>The root dock; populated after <see cref="CreateLayout"/> is called.</summary>
    public IRootDock? RootDock { get; private set; }

    public StyloagentDockFactory(AgentPaneViewModel? agentPane = null, BusViewModel? busViewModel = null)
    {
        _agentPane = agentPane;
        _busViewModel = busViewModel;
    }

    public override IRootDock CreateLayout()
    {
        // Left bus tool
        var leftBusTool = new Tool
        {
            Id = "LeftBus",
            Title = "Signal Bus",
            Context = _busViewModel,
        };

        // Right bus tool
        var rightBusTool = new Tool
        {
            Id = "RightBus",
            Title = "Bus Threads",
            Context = _busViewModel,
        };

        // Agent pane document
        var agentDocument = new Document
        {
            Id = "AgentPane",
            Title = _agentPane?.DisplayName ?? "Agent",
            Context = _agentPane,
        };

        // Left ToolDock
        var leftToolDock = new ToolDock
        {
            Id = "LeftToolDock",
            Title = "Left Tools",
            Alignment = Alignment.Left,
            Proportion = 0.20,
            VisibleDockables = CreateList<IDockable>(leftBusTool),
            ActiveDockable = leftBusTool,
            DefaultDockable = leftBusTool,
        };

        // Document dock (center)
        var documentDock = new DocumentDock
        {
            Id = "DocumentDock",
            Title = "Documents",
            Proportion = 0.60,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(agentDocument),
            ActiveDockable = agentDocument,
            DefaultDockable = agentDocument,
        };

        // Right ToolDock
        var rightToolDock = new ToolDock
        {
            Id = "RightToolDock",
            Title = "Right Tools",
            Alignment = Alignment.Right,
            Proportion = 0.20,
            VisibleDockables = CreateList<IDockable>(rightBusTool),
            ActiveDockable = rightBusTool,
            DefaultDockable = rightBusTool,
        };

        // Horizontal ProportionalDock
        var rootLayout = new ProportionalDock
        {
            Id = "RootLayout",
            Title = "Root Layout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                leftToolDock,
                new ProportionalDockSplitter(),
                documentDock,
                new ProportionalDockSplitter(),
                rightToolDock),
            ActiveDockable = documentDock,
        };

        // Root dock
        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "Root";
        rootDock.VisibleDockables = CreateList<IDockable>(rootLayout);
        rootDock.ActiveDockable = rootLayout;
        rootDock.DefaultDockable = rootLayout;
        rootDock.IsFocusableRoot = true;

        // Expose for runtime document addition
        DocumentDock = documentDock;
        RootDock = rootDock;

        return rootDock;
    }
}
