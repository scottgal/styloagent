using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Styloagent.App.ViewModels;

namespace Styloagent.App.Dock;

/// <summary>How the centre region arranges the agent panes.</summary>
public enum CockpitLayoutMode
{
    /// <summary>All panes stacked as tabs in one document dock (the default).</summary>
    Tabs,
    /// <summary>Every pane tiled in an even grid (rows of two).</summary>
    Tile,
    /// <summary>The starter (overview) pane full-width on top; the rest tiled in a grid below.</summary>
    AutoTile,
}

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

    /// <summary>
    /// Builds a fresh centre layout for <paramref name="mode"/> from the current <paramref name="panes"/>,
    /// reusing the pane view-models as documents (their terminals persist — the view re-attaches to
    /// <c>CurrentPty</c> when it re-renders). Tabs → one document dock; Tile → an even grid; AutoTile →
    /// the starter full-width on top with the rest gridded below. <see cref="DocumentDock"/> is repointed
    /// at the primary dock so runtime document adds still have a target.
    /// </summary>
    public IRootDock BuildLayout(IReadOnlyList<AgentPaneViewModel> panes, CockpitLayoutMode mode)
    {
        IDock centre = mode switch
        {
            CockpitLayoutMode.Tile     => Grid(panes),
            CockpitLayoutMode.AutoTile => AutoTile(panes),
            _                          => TabsDock(panes),
        };

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "Root";
        rootDock.VisibleDockables = CreateList<IDockable>(centre);
        rootDock.ActiveDockable = centre;
        rootDock.DefaultDockable = centre;
        rootDock.IsFocusableRoot = true;

        RootDock = rootDock;
        return rootDock;
    }

    // ── layout builders ──────────────────────────────────────────────────────

    /// <summary>One document dock holding every pane as a tab (the classic layout).</summary>
    private DocumentDock TabsDock(IReadOnlyList<AgentPaneViewModel> panes)
    {
        var dock = new DocumentDock
        {
            Id = "DocumentDock",
            Title = "Agents",
            Proportion = double.NaN,
            IsCollapsable = false,
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>(),
        };
        foreach (var p in panes) dock.VisibleDockables!.Add(p);
        var active = panes.Count > 0 ? panes[0] : null;
        dock.ActiveDockable = active;
        dock.DefaultDockable = active;
        DocumentDock = dock;   // the shared tab dock is the add target
        return dock;
    }

    /// <summary>The starter (first depth-0 pane) full-width on top; the remaining panes gridded below.</summary>
    private IDock AutoTile(IReadOnlyList<AgentPaneViewModel> panes)
    {
        if (panes.Count <= 1) return Grid(panes);

        var starter = panes.FirstOrDefault(p => p.Depth == 0) ?? panes[0];
        var rest = panes.Where(p => !ReferenceEquals(p, starter)).ToList();

        var column = new ProportionalDock
        {
            Orientation = Orientation.Vertical,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                OneDoc(starter),
                new ProportionalDockSplitter(),
                Grid(rest)),
        };
        return column;
    }

    /// <summary>Tiles panes in an even grid: rows of up to two columns, stacked vertically.</summary>
    private IDock Grid(IReadOnlyList<AgentPaneViewModel> panes)
    {
        if (panes.Count == 0) return TabsDock(panes);       // empty — a bare document dock
        if (panes.Count == 1) { var d = OneDoc(panes[0]); DocumentDock = d; return d; }

        // Chunk into rows of two.
        var rows = new List<IReadOnlyList<AgentPaneViewModel>>();
        for (int i = 0; i < panes.Count; i += 2)
            rows.Add(panes.Skip(i).Take(2).ToList());

        var column = new ProportionalDock
        {
            Orientation = Orientation.Vertical,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(),
        };
        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) column.VisibleDockables!.Add(new ProportionalDockSplitter());
            column.VisibleDockables!.Add(Row(rows[r]));
        }
        DocumentDock = FirstDocumentDock(column);   // add target = the first tile
        return column;
    }

    /// <summary>One tiled row: its panes side-by-side as separate document docks split horizontally.</summary>
    private IDock Row(IReadOnlyList<AgentPaneViewModel> panes)
    {
        if (panes.Count == 1) return OneDoc(panes[0]);

        var row = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(),
        };
        for (int i = 0; i < panes.Count; i++)
        {
            if (i > 0) row.VisibleDockables!.Add(new ProportionalDockSplitter());
            row.VisibleDockables!.Add(OneDoc(panes[i]));
        }
        return row;
    }

    /// <summary>A single-pane document dock (one tile). Collapsable so that emptying a TILE (a nested
    /// dock with siblings) removes it and the layout reflows; the sole centre dock is protected in
    /// <see cref="CollapseDock"/> by its root owner.</summary>
    private DocumentDock OneDoc(AgentPaneViewModel pane) => new()
    {
        Id = "doc-" + pane.Prefix,
        Title = pane.DisplayName,
        Proportion = double.NaN,
        IsCollapsable = true,
        CanCreateDocument = false,
        VisibleDockables = CreateList<IDockable>(pane),
        ActiveDockable = pane,
        DefaultDockable = pane,
    };

    /// <summary>
    /// Fix E: when the last document in a tiled area is closed, collapse the now-empty tile so the layout
    /// reflows into the freed space. Guards two cases: (1) never collapse the SOLE centre dock — its owner
    /// is the root, and you always want a document surface; (2) if the collapsed tile was the current
    /// add-target, re-point <see cref="DocumentDock"/> at a surviving document dock so later
    /// <c>OpenDocument…</c> calls still land somewhere visible.
    /// </summary>
    public override void CollapseDock(IDock dock)
    {
        if (dock.Owner is IRootDock) return;   // the centre document surface must persist

        base.CollapseDock(dock);

        if (ReferenceEquals(dock, DocumentDock) && RootDock is not null)
            DocumentDock = FirstDocumentDock(RootDock);
    }

    /// <summary>
    /// Finds the NESTED empty document docks in a layout tree — leftover split/tile regions holding no
    /// documents — for the "Close empty docks" tidy action. A root-level document dock (the sole centre
    /// surface) is deliberately excluded: you always want somewhere to open documents. Mirrors the guard
    /// in <see cref="CollapseDock"/>.
    /// </summary>
    internal static IReadOnlyList<IDock> EmptyCollapsibleDocks(IDock root)
    {
        var result = new List<IDock>();
        Walk(root, parentIsRoot: false);
        return result;

        void Walk(IDockable node, bool parentIsRoot)
        {
            if (node is not IDock d) return;
            bool empty = d is DocumentDock && (d.VisibleDockables is null || d.VisibleDockables.Count == 0);
            if (empty && !parentIsRoot) result.Add(d);
            if (d.VisibleDockables is not null)
                foreach (var child in d.VisibleDockables)
                    Walk(child, parentIsRoot: d is IRootDock);
        }
    }

    /// <summary>Depth-first find of the first <see cref="DocumentDock"/> in a built tree.</summary>
    private static DocumentDock? FirstDocumentDock(IDock dock)
    {
        if (dock is DocumentDock dd) return dd;
        if (dock.VisibleDockables is null) return null;
        foreach (var child in dock.VisibleDockables)
            if (child is IDock cd && FirstDocumentDock(cd) is { } found)
                return found;
        return null;
    }
}
