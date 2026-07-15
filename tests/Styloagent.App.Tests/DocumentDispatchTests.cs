using Styloagent.App.ViewModels;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// The viewer-by-type dispatch behind the top-bar search and the drag-onto-the-doc-surface drop:
/// markdown files open in the markdown viewer, everything else in the read-only source viewer.
/// </summary>
public class DocumentDispatchTests
{
    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("docs/plan.markdown", true)]
    [InlineData("README.MD", true)]          // case-insensitive
    [InlineData("Program.cs", false)]
    [InlineData("appsettings.json", false)]
    [InlineData("Makefile", false)]          // no extension → source
    public void ViewerKindForPath_picks_markdown_for_md_else_source(string path, bool expectMarkdown)
        => Assert.Equal(
            expectMarkdown ? DocViewerKind.Markdown : DocViewerKind.Source,
            MainWindowViewModel.ViewerKindForPath(path));
}
