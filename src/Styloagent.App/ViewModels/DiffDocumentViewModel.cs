using System.Linq;
using Dock.Model.Mvvm.Controls;
using Styloagent.Core.Docs;

namespace Styloagent.App.ViewModels;

/// <summary>One rendered diff row: the (prefixed) text plus its background and foreground colours.</summary>
public sealed record DiffRow(string Text, string BgHex, string FgHex);

/// <summary>
/// A read-only before/after view of an agent's pending edit, shown as a dock document: the
/// <see cref="LineDiff"/> of the Edit's old_string → new_string, with removed lines on dark red and
/// added lines on dark green.
/// </summary>
public sealed class DiffDocumentViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    // Present immediately (a live terminal starves Dock's Background deferred queue).
    public bool DeferContentPresentation => false;

    /// <summary>The diff, one coloured row per line.</summary>
    public IReadOnlyList<DiffRow> Rows { get; }

    public DiffDocumentViewModel(string title, string oldText, string newText)
    {
        Rows = LineDiff.Compute(oldText, newText).Select(l => l.Kind switch
        {
            DiffKind.Removed => new DiffRow("- " + l.Text, "#3A1E1E", "#E6A6A6"),
            DiffKind.Added   => new DiffRow("+ " + l.Text, "#183A22", "#A6E0B0"),
            _                => new DiffRow("  " + l.Text, "#00000000", "#9A9AB0"),
        }).ToList();

        Title = "Δ " + title;
        CanFloat = true;
    }
}
