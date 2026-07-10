using Styloagent.App.ViewModels;
using Xunit;

namespace Styloagent.App.Tests;

public class DiagramCommandsTests
{
    [Fact]
    public async Task ShowSystemMap_opens_a_flowchart_diagram_doc()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.ShowSystemMapCommand.Execute(null);

            var diagrams = vm.OpenDiagramsForTest();
            Assert.NotEmpty(diagrams);
            var doc = diagrams[diagrams.Count - 1];
            Assert.Equal(DiagramKind.SystemMap, doc.Kind);
            Assert.Contains("graph TD", doc.Markdown);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Live_diagram_regenerates_on_a_fleet_change()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.ShowSystemMapCommand.Execute(null);
            var diagrams = vm.OpenDiagramsForTest();
            var doc = diagrams[diagrams.Count - 1];
            doc.Live = true;
            var before = doc.Markdown;

            vm.AddAgentCommand.Execute(null);           // fleet changes → a new pane
            vm.RegenerateLiveDiagramsForTest();         // deterministic stand-in for the debounce tick

            Assert.NotEqual(before, doc.Markdown);      // regenerated with the new agent
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ShowBusSequence_opens_a_flowchart()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());
            vm.ShowBusSequenceCommand.Execute(null);

            var diagrams = vm.OpenDiagramsForTest();
            Assert.NotEmpty(diagrams);
            var doc = diagrams[diagrams.Count - 1];
            Assert.Equal(DiagramKind.BusSequence, doc.Kind);
            Assert.Contains("graph LR", doc.Markdown);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
