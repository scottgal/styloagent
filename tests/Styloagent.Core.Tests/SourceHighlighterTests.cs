using System.Linq;
using Styloagent.Core.Docs;

namespace Styloagent.Core.Tests;

public class SourceHighlighterTests
{
    [Fact]
    public void Classifies_keywords_strings_comments_and_numbers()
    {
        var spans = SourceHighlighter.Highlight("public int x = 42; // note\nvar s = \"hi\";");

        Assert.Contains(spans, s => s.Kind == SourceTokenKind.Keyword && s.Text == "public");
        Assert.Contains(spans, s => s.Kind == SourceTokenKind.Keyword && s.Text == "int");
        Assert.Contains(spans, s => s.Kind == SourceTokenKind.Number && s.Text == "42");
        Assert.Contains(spans, s => s.Kind == SourceTokenKind.Comment && s.Text.Contains("note"));
        Assert.Contains(spans, s => s.Kind == SourceTokenKind.String && s.Text == "\"hi\"");
    }

    [Fact]
    public void Spans_reconstruct_the_original_text_losslessly()
    {
        const string text = "if (a) { return b + 1; } /* blk */ // ok\nx = 'c';";
        var joined = string.Concat(SourceHighlighter.Highlight(text).Select(s => s.Text));
        Assert.Equal(text, joined);
    }

    [Fact]
    public void Keywords_inside_strings_and_comments_are_not_recoloured()
    {
        var spans = SourceHighlighter.Highlight("\"public\" // return");
        // The word "public" appears only inside a string; "return" only inside a comment.
        Assert.DoesNotContain(spans, s => s.Kind == SourceTokenKind.Keyword);
    }

    [Fact]
    public void Empty_input_yields_no_spans()
        => Assert.Empty(SourceHighlighter.Highlight(""));
}
