namespace Styloagent.App.ViewModels;

/// <summary>Which registered document viewer the doc-surface dispatch opens a file in.</summary>
internal enum DocViewerKind
{
    /// <summary>Rendered markdown (<c>.md</c> / <c>.markdown</c>).</summary>
    Markdown,

    /// <summary>Read-only, syntax-highlighted source view (everything else).</summary>
    Source,
}
