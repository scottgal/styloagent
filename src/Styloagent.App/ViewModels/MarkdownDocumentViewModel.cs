using CommunityToolkit.Mvvm.ComponentModel;

namespace Styloagent.App.ViewModels;

/// <summary>
/// View-model for a rendered markdown document tab in the centre dock.
/// Reads the file on construction; call <see cref="Refresh"/> to re-read after changes.
/// </summary>
public sealed partial class MarkdownDocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _markdown;

    public string Title { get; }
    public string FullPath { get; }
    public string SourcePath { get; }

    public MarkdownDocumentViewModel(string title, string fullPath)
    {
        Title = title;
        FullPath = fullPath;
        SourcePath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        _markdown = ReadFile(fullPath);
    }

    /// <summary>Re-reads the file and updates <see cref="Markdown"/>.</summary>
    public void Refresh() => Markdown = ReadFile(FullPath);

    private static string ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }
}
