using System.Globalization;
using Styloagent.App.Converters;
using Styloagent.App.ViewModels;
using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// The document-tab header is shared across every dockable type; the per-agent ⋯ actions menu shows only
/// on agent tabs. <see cref="IsAgentPaneConverter"/> is that gate (operator fix: name+actions+zoom moved
/// onto the tab, killing the redundant pane-chrome header row).
/// </summary>
public class IsAgentPaneConverterTests
{
    private static readonly IsAgentPaneConverter C = IsAgentPaneConverter.Instance;
    private static object? Convert(object? v) => C.Convert(v, typeof(bool), null, CultureInfo.InvariantCulture);

    [Fact]
    public void True_for_an_agent_pane()
    {
        var entry = new AgentManifestEntry("foss-", "/repo", "/repo", "", "", "", AgentTransport.Local);
        var pane = new AgentPaneViewModel(new AgentSession(entry, new FakeLauncher(), new FakeWatcher()),
            entry, "foss", "#888888");
        Assert.Equal(true, Convert(pane));
    }

    [Fact]
    public void False_for_a_markdown_document_and_for_null()
    {
        Assert.Equal(false, Convert(MarkdownDocumentViewModel.FromMarkdown("doc", "# hi")));
        Assert.Equal(false, Convert(null));
        Assert.Equal(false, Convert("some string"));
    }
}
