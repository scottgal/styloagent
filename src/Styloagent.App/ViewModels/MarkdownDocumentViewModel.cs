using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace Styloagent.App.ViewModels;

/// <summary>
/// View-model for a rendered markdown document tab in the centre dock. It IS a Dock
/// <see cref="Document"/> so the DockControl hosts it directly and renders it through the App.axaml
/// DataTemplate (MarkdownDocumentViewModel → MarkdownDocumentView). <see cref="Document.Title"/> is
/// the tab caption. Reads the file on construction; call <see cref="Refresh"/> to re-read after
/// changes. Use <see cref="FromMarkdown"/> to create an in-memory instance without a backing file.
/// </summary>
public partial class MarkdownDocumentViewModel : Document
{
    [ObservableProperty]
    private string _markdown;

    public string FullPath { get; }
    public string SourcePath { get; }

    public MarkdownDocumentViewModel(string title, string fullPath)
    {
        Id = title;
        Title = title;
        FullPath = fullPath;
        SourcePath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        _markdown = ReadFile(fullPath);
    }

    /// <summary>Protected ctor for subclasses and in-memory instances (no file read).</summary>
    protected MarkdownDocumentViewModel(string title)
    {
        Id = title;
        Title = title;
        FullPath = string.Empty;
        SourcePath = string.Empty;
        _markdown = string.Empty;
    }

    /// <summary>Creates an in-memory view-model pre-populated with <paramref name="markdown"/>; no backing file.</summary>
    public static MarkdownDocumentViewModel FromMarkdown(string title, string markdown)
    {
        var vm = new MarkdownDocumentViewModel(title);
        vm.Markdown = markdown;
        return vm;
    }

    /// <summary>Re-reads the file and updates <see cref="Markdown"/>.</summary>
    public void Refresh() => Markdown = ReadFile(FullPath);

    private static string ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }
}
