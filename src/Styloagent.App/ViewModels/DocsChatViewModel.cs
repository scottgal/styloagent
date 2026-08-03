using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Styloagent.Core.Retrieval;

namespace Styloagent.App.ViewModels;

public sealed record DocsChatMessage(bool IsUser, string Text, IReadOnlyList<ContextHit>? Sources = null);

/// <summary>A selectable document source for the scoping checklist.</summary>
public sealed partial class SourceToggle : ObservableObject
{
    public string Key { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;

    public SourceToggle(string key, string label, bool isChecked = false)
    {
        Key = key;
        Label = label;
        _isChecked = isChecked;
    }
}

/// <summary>A local, grounded chat session over the project document library.</summary>
public sealed partial class DocsChatViewModel : Document, global::Dock.Controls.DeferredContentControl.IDeferredContentPresentation
{
    private readonly Func<string, IReadOnlyCollection<string>, Task<DocumentAnswer>> _answer;
    private readonly string _modelLabel;
    public bool DeferContentPresentation => false;
    public ObservableCollection<DocsChatMessage> Messages { get; } = new();
    public ObservableCollection<SourceToggle> Sources { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private string _draft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isThinking;

    /// <summary>Status line: idle → shows model; thinking → shows progress.</summary>
    public string Status => IsThinking
        ? $"Synthesizing with {_modelLabel}…"
        : $"Grounded in project documentation · {_modelLabel}";

    /// <summary>Input box and Send button are enabled only when not thinking.</summary>
    public bool IsReady => !IsThinking && !string.IsNullOrWhiteSpace(Draft);

    public DocsChatViewModel(Func<string, IReadOnlyCollection<string>, Task<DocumentAnswer>> answer, string modelLabel = "local model")
    {
        _answer = answer;
        _modelLabel = modelLabel;
        Id = "DocsChat-" + Guid.NewGuid().ToString("N");
        Title = "Docs chat";
        Messages.Add(new DocsChatMessage(false, "Ask a question about this project's documentation. Answers are grounded in retrieved document sections and show their sources."));
        Sources.Add(new SourceToggle("docs", "Documents", true));
        Sources.Add(new SourceToggle("memory", "Memory", false));
        Sources.Add(new SourceToggle("bus", "Bus", false));
        Sources.Add(new SourceToggle("issues", "Issues", false));
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var question = Draft.Trim();
        if (question.Length == 0 || IsThinking) return;
        Draft = "";
        Messages.Add(new DocsChatMessage(true, question));
        var thinkingMsg = new DocsChatMessage(false, "…");
        Messages.Add(thinkingMsg);
        IsThinking = true;
        try
        {
            var selectedSources = Sources.Where(s => s.IsChecked).Select(s => s.Key).ToList();
            var answer = await _answer(question, selectedSources);
            Messages.Remove(thinkingMsg);
            Messages.Add(new DocsChatMessage(false, answer.Markdown, answer.Sources));
        }
        catch (Exception ex)
        {
            Messages.Remove(thinkingMsg);
            Messages.Add(new DocsChatMessage(false, $"I couldn't search the document library: {ex.Message}"));
        }
        finally { IsThinking = false; }
    }
}
