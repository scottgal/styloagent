using System.Linq;
using Styloagent.Core.Docs;

namespace Styloagent.Core.Tests;

public class LineDiffTests
{
    [Fact]
    public void Keeps_unchanged_lines_and_marks_the_replaced_one()
    {
        var diff = LineDiff.Compute("a\nb\nc", "a\nB\nc");

        Assert.Equal(DiffKind.Same, diff[0].Kind);      // a
        Assert.Contains(diff, d => d.Kind == DiffKind.Removed && d.Text == "b");
        Assert.Contains(diff, d => d.Kind == DiffKind.Added && d.Text == "B");
        Assert.Equal(DiffKind.Same, diff[^1].Kind);     // c
    }

    [Fact]
    public void Pure_additions_are_all_added()
    {
        var diff = LineDiff.Compute("", "x\ny");
        Assert.All(diff, d => Assert.Equal(DiffKind.Added, d.Kind));
    }

    [Fact]
    public void Identical_text_is_all_same()
    {
        var diff = LineDiff.Compute("one\ntwo", "one\ntwo");
        Assert.All(diff, d => Assert.Equal(DiffKind.Same, d.Kind));
    }

    [Fact]
    public void Removed_lines_recover_the_old_text_added_lines_the_new()
    {
        var diff = LineDiff.Compute("keep\ndrop\nkeep2", "keep\nkeep2\nadd");

        var oldBack = string.Join('\n', diff.Where(d => d.Kind != DiffKind.Added).Select(d => d.Text));
        var newBack = string.Join('\n', diff.Where(d => d.Kind != DiffKind.Removed).Select(d => d.Text));

        Assert.Equal("keep\ndrop\nkeep2", oldBack);
        Assert.Equal("keep\nkeep2\nadd", newBack);
    }
}
