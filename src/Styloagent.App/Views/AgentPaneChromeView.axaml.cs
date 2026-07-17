using Avalonia.Controls;

namespace Styloagent.App.Views;

/// <summary>
/// Cockpit-owned pane header chrome (0b): the consolidated ⋯ actions menu, terminal-zoom slider, and theme
/// picker for one agent pane. Hosted by <c>AgentPaneView</c> (session-) over an <c>AgentPaneViewModel</c>
/// DataContext. Kept a separate control so the agent-log "Log (this agent)" entry lands here with no
/// cross-owner edit.
/// </summary>
public partial class AgentPaneChromeView : UserControl
{
    public AgentPaneChromeView() => InitializeComponent();
}
