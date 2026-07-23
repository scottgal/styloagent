using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Styloagent.Core.Retrieval;

namespace Styloagent.App.ViewModels;

public sealed record DocsChatMessage(bool IsUser, string Text, IReadOnlyList<ContextHit>? Sources = null);

/// <summary>A local, grounded chat session over the project document library.</summary>
public sealed partial class DocsChatViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    private readonly Func<string, Task<DocumentAnswer>> _answer;
    public bool DeferContentPresentation => false;
    public ObservableCollection<DocsChatMessage> Messages { get; } = new();

    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private bool _isThinking;
    public string Status => IsThinking ? "Searching project documents…" : "Grounded in project documentation · gemma4:4b";
    partial void OnIsThinkingChanged(bool value) => OnPropertyChanged(nameof(Status));

    public DocsChatViewModel(Func<string, Task<DocumentAnswer>> answer)
    {
        _answer = answer;
        Id = "DocsChat-" + Guid.NewGuid().ToString("N");
        Title = "Docs chat";
        Messages.Add(new DocsChatMessage(false, "Ask a question about this project's documentation. Answers are grounded in retrieved document sections and show their sources."));
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var question = Draft.Trim();
        if (question.Length == 0 || IsThinking) return;
        Draft = "";
        Messages.Add(new DocsChatMessage(true, question));
        IsThinking = true;
        try
        {
            var answer = await _answer(question);
            Messages.Add(new DocsChatMessage(false, answer.Markdown, answer.Sources));
        }
        catch (Exception ex)
        {
            Messages.Add(new DocsChatMessage(false, $"I couldn't search the document library: {ex.Message}"));
        }
        finally { IsThinking = false; }
    }
}
