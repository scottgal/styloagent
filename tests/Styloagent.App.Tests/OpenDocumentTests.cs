using Dock.Model.Mvvm.Controls;
using Styloagent.App.ViewModels;
using Styloagent.Core.Attention;

namespace Styloagent.App.Tests;

/// <summary>
/// open_document (bus-'s DocumentOpenHub → cockpit): an agent's request opens the file as a document pane
/// on the doc surface, and the asker + reason land on the activity timeline. Drives the same handler the
/// hub fires; the MCP verb + UI-thread Post are the only untested seams (glue), so this covers the behavior.
/// </summary>
public class OpenDocumentTests
{
    [Fact]
    public async Task OpenDocumentRequest_OpensTheFileAsADocumentPane_AndLogsIt()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            var dock = vm.DocumentDock!;
            var docsBefore = dock.VisibleDockables!.OfType<Document>().Count();
            var timelineBefore = vm.TimelineCount;

            var md = Path.Combine(root, "note.md");
            File.WriteAllText(md, "# hello from an agent");
            vm.RaiseDocumentOpenForTest(new DocumentOpenRequest("stylo-", md, "look at this"));

            // A new document pane opened for the file (markdown → MarkdownDocumentViewModel).
            var docs = dock.VisibleDockables!.OfType<Document>().ToList();
            Assert.Equal(docsBefore + 1, docs.Count);
            Assert.Contains(docs, d => d is MarkdownDocumentViewModel);
            // The open was surfaced on the activity timeline (who + why).
            Assert.Equal(timelineBefore + 1, vm.TimelineCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
