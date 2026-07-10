using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Dock;

/// <summary>
/// Dock factory for the Styloagent shell CENTRE region: a <see cref="Dock.Model.Mvvm.Controls.DocumentDock"/>
/// hosting each agent terminal as a dockable document (tabs, float, split). The Agents roster and the
/// bus feed are fixed side panels in the window Grid — not tool docks — per the "agents left, one bus
/// right" layout, so this factory intentionally builds a document-only layout.
/// </summary>
public sealed class StyloagentDockFactory : Factory
{
    private readonly AgentPaneViewModel? _agentPane;

    /// <summary>The centre DocumentDock; populated after <see cref="CreateLayout"/> is called.</summary>
    public DocumentDock? DocumentDock { get; private set; }

    /// <summary>The root dock; populated after <see cref="CreateLayout"/> is called.</summary>
    public IRootDock? RootDock { get; private set; }

    /// <param name="agentPane">The first agent pane, hosted as the initial document (may be null).</param>
    /// <param name="busViewModel">Unused — retained for call-site compatibility; the bus is a Grid panel.</param>
    public StyloagentDockFactory(AgentPaneViewModel? agentPane = null, BusViewModel? busViewModel = null)
    {
        _agentPane = agentPane;
    }

    public override IRootDock CreateLayout()
    {
        var documentDock = new DocumentDock
        {
            Id = "DocumentDock",
            Title = "Agents",
            Proportion = 1.0,
            IsCollapsable = false,
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>(),
        };

        // Seed the first agent as a document when one is supplied. The pane view-model IS a Dock
        // Document (it inherits Document), so it is added directly — no wrapper — and the DockControl
        // renders it via the App.axaml DataTemplate (AgentPaneViewModel → AgentPaneView).
        if (_agentPane is not null)
        {
            documentDock.VisibleDockables!.Add(_agentPane);
            documentDock.ActiveDockable = _agentPane;
            documentDock.DefaultDockable = _agentPane;
        }

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "Root";
        rootDock.VisibleDockables = CreateList<IDockable>(documentDock);
        rootDock.ActiveDockable = documentDock;
        rootDock.DefaultDockable = documentDock;
        rootDock.IsFocusableRoot = true;

        // Expose for runtime document addition.
        DocumentDock = documentDock;
        RootDock = rootDock;

        return rootDock;
    }
}
