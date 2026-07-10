using Styloagent.App.ViewModels;

namespace Styloagent.App.Tests;

public class DiagramDocumentViewModelTests
{
    [Fact]
    public void FromMarkdown_sets_markdown_title_and_empty_source_path()
    {
        var doc = MarkdownDocumentViewModel.FromMarkdown("System Map", "# hi\n\nbody");

        Assert.Contains("body", doc.Markdown);
        Assert.Equal(string.Empty, doc.SourcePath);
        Assert.Equal("System Map", doc.Title);
    }

    [Fact]
    public void DiagramDocumentViewModel_generates_on_construction_and_regenerates_on_command()
    {
        var calls = 0;
        var doc = new DiagramDocumentViewModel("System Map", DiagramKind.SystemMap, () => $"gen {++calls}");

        Assert.Equal("gen 1", doc.Markdown);

        doc.RegenerateCommand.Execute(null);

        Assert.Equal("gen 2", doc.Markdown);
        Assert.Equal(DiagramKind.SystemMap, doc.Kind);
    }
}
