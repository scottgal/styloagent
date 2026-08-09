using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.Core.Model;
using Styloagent.Core.Projects;

namespace Styloagent.App.ViewModels;

/// <summary>The startup screen: start a NEW system from a one-line goal, or open/reopen an existing one.</summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly RecentProjectsStore _recents;
    private readonly string _recentsPath;
    private readonly IFolderPicker _picker;
    private readonly Action<string> _onProjectChosen;
    private readonly Action<AgentRuntimeKind>? _onRuntimeChanged;

    [ObservableProperty]
    private ObservableCollection<string> _recent = new();

    /// <summary>The "build a system like X which does Y" goal for the New System path.</summary>
    [ObservableProperty]
    private string _newSystemDescription = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClaudeFirst))]
    [NotifyPropertyChangedFor(nameof(IsCodexFirst))]
    [NotifyPropertyChangedFor(nameof(IsKiloFirst))]
    [NotifyPropertyChangedFor(nameof(IsClaudeDeepSeekFirst))]
    private AgentRuntimeKind _selectedRuntime;

    // Settable so the cards can bind TwoWay: clicking a card drives SelectedRuntime directly AND the
    // visual stays in lockstep (a radio group). The setter only reacts to `true`; a click that turns an
    // already-selected card off is a no-op, and SetRuntimeMode re-pushes to re-check it.
    public bool IsClaudeFirst { get => SelectedRuntime == AgentRuntimeKind.Claude; set { if (value) SelectedRuntime = AgentRuntimeKind.Claude; } }
    public bool IsCodexFirst { get => SelectedRuntime == AgentRuntimeKind.Codex; set { if (value) SelectedRuntime = AgentRuntimeKind.Codex; } }
    public bool IsKiloFirst { get => SelectedRuntime == AgentRuntimeKind.Kilo; set { if (value) SelectedRuntime = AgentRuntimeKind.Kilo; } }
    public bool IsClaudeDeepSeekFirst { get => SelectedRuntime == AgentRuntimeKind.ClaudeDeepSeek; set { if (value) SelectedRuntime = AgentRuntimeKind.ClaudeDeepSeek; } }

    public WelcomeViewModel(RecentProjectsStore recents, string recentsPath, IFolderPicker picker,
        Action<string> onProjectChosen,
        AgentRuntimeKind initialRuntime = AgentRuntimeKind.Kilo,
        Action<AgentRuntimeKind>? onRuntimeChanged = null)
    {
        _recents = recents;
        _recentsPath = recentsPath;
        _picker = picker;
        _onProjectChosen = onProjectChosen;
        _onRuntimeChanged = onRuntimeChanged;
        _selectedRuntime = initialRuntime;
    }

    public async Task LoadRecentsAsync()
    {
        Recent.Clear();
        foreach (var p in await _recents.LoadAsync(_recentsPath))
            Recent.Add(p);
    }

    [RelayCommand]
    private void SetRuntimeMode(string? mode)
    {
        var kind = AgentRuntime.Parse(mode);
        SelectedRuntime = kind;
        // Re-push every TwoWay Is*Checked binding: a click on the ALREADY-selected card flips its local
        // IsChecked off, and since the source value did not change the binding would never write it back —
        // leaving the selector with nothing visibly selected. Forcing the notifications keeps the visual
        // state in lockstep with the selection (and re-checks the clicked card).
        OnPropertyChanged(nameof(IsClaudeFirst));
        OnPropertyChanged(nameof(IsCodexFirst));
        OnPropertyChanged(nameof(IsKiloFirst));
        OnPropertyChanged(nameof(IsClaudeDeepSeekFirst));
        _onRuntimeChanged?.Invoke(kind);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        var path = await _picker.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
            _onProjectChosen(path);
    }

    /// <summary>
    /// New System: pick an (empty) folder, scaffold it, and drop a brief that tells the architect
    /// agent to research + clarify + define the shape + build the first feature from the goal.
    /// </summary>
    [RelayCommand]
    private async Task NewSystem()
    {
        if (string.IsNullOrWhiteSpace(NewSystemDescription))
            return;

        var path = await _picker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var cfg = ProjectScaffolder.Ensure(path);
        await File.WriteAllTextAsync(cfg.BriefPath, DefaultTemplates.NewSystemBrief(NewSystemDescription));
        _onProjectChosen(path);
    }

    [RelayCommand]
    private void OpenRecent(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _onProjectChosen(path);
    }
}
