using Dock.Model.Mvvm.Controls;

namespace Styloagent.App.ViewModels;

/// <summary>
/// A read-only source file shown as a dock document in an AvaloniaEdit editor with TextMate
/// (VS Code-grade) syntax highlighting. Opened from the activity timeline when you click a file an
/// agent touched. Reads the file on construction; <see cref="Extension"/> selects the grammar.
/// </summary>
public sealed class SourceDocumentViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    // Present immediately rather than via Dock's Background-priority deferred queue (starved by a live
    // terminal) — same rationale as the markdown/agent documents.
    public bool DeferContentPresentation => false;

    /// <summary>Absolute path of the file.</summary>
    public string FilePath { get; }

    /// <summary>File contents (or an error placeholder — never throws into the UI).</summary>
    public string Text { get; }

    /// <summary>File extension (e.g. <c>.cs</c>) used to pick the TextMate grammar.</summary>
    public string Extension { get; }

    public SourceDocumentViewModel(string filePath)
    {
        FilePath = filePath;
        Extension = System.IO.Path.GetExtension(filePath);
        try
        {
            Text = System.IO.File.Exists(filePath)
                ? System.IO.File.ReadAllText(filePath)
                : $"(file not found)\n{filePath}";
        }
        catch (System.Exception ex)
        {
            Text = $"(could not read {filePath})\n{ex.Message}";
        }

        Title = System.IO.Path.GetFileName(filePath);
        CanFloat = true;
    }
}
