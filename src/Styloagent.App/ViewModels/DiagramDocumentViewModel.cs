using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Styloagent.App.ViewModels;

/// <summary>The kind of diagram rendered in a <see cref="DiagramDocumentViewModel"/>.</summary>
public enum DiagramKind
{
    SystemMap,
    BusSequence,
}

/// <summary>
/// View-model for a generated diagram document tab.
/// Calls <paramref name="generate"/> on construction to produce the initial markdown;
/// call <see cref="RegenerateCommand"/> to re-run the generator.
/// </summary>
public sealed partial class DiagramDocumentViewModel : MarkdownDocumentViewModel
{
    private readonly Func<string> _generate;

    [ObservableProperty]
    private bool _live;

    public DiagramDocumentViewModel(string title, DiagramKind kind, Func<string> generate)
        : base(title)
    {
        Kind = kind;
        _generate = generate;
        Markdown = _generate();
    }

    public DiagramKind Kind { get; }

    [RelayCommand]
    private void Regenerate() => Markdown = _generate();
}
