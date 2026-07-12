using System;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Styloagent.App.ViewModels;
using Styloagent.Core.Docs;

namespace Styloagent.App.Views;

/// <summary>
/// Code-behind for the read-only source viewer: tokenizes the file with <see cref="SourceHighlighter"/>
/// and renders coloured inline runs into a <see cref="SelectableTextBlock"/> (which — unlike
/// AvaloniaEdit's custom TextView — renders reliably in the off-screen capture, and lets us pick
/// dark-theme colours).
/// </summary>
public partial class SourceDocumentView : UserControl
{
    private const int MaxChars = 200_000;   // cap huge files so the inline count stays sane

    private static readonly IBrush DefaultBrush = Brush.Parse("#D6D6E8");
    private static readonly IBrush KeywordBrush = Brush.Parse("#569CD6");
    private static readonly IBrush StringBrush  = Brush.Parse("#CE9178");
    private static readonly IBrush CommentBrush = Brush.Parse("#6A9955");
    private static readonly IBrush NumberBrush  = Brush.Parse("#B5CEA8");

    public SourceDocumentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Code.Inlines?.Clear();
        if (DataContext is not SourceDocumentViewModel vm) return;

        var text = vm.Text;
        var truncated = text.Length > MaxChars;
        if (truncated) text = text[..MaxChars];

        var inlines = new InlineCollection();
        foreach (var span in SourceHighlighter.Highlight(text))
            inlines.Add(new Run(span.Text) { Foreground = BrushFor(span.Kind) });
        if (truncated)
            inlines.Add(new Run("\n\n… (truncated)") { Foreground = CommentBrush });

        Code.Inlines = inlines;
    }

    private static IBrush BrushFor(SourceTokenKind kind) => kind switch
    {
        SourceTokenKind.Keyword => KeywordBrush,
        SourceTokenKind.String  => StringBrush,
        SourceTokenKind.Comment => CommentBrush,
        SourceTokenKind.Number  => NumberBrush,
        _                       => DefaultBrush,
    };
}
