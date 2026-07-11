using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.Core.Projects;

namespace Styloagent.App.ViewModels;

/// <summary>The startup screen: start a NEW system from a one-line goal, or open/reopen an existing one.</summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly RecentProjectsStore _recents;
    private readonly string _recentsPath;
    private readonly IFolderPicker _picker;
    private readonly Action<string> _onProjectChosen;

    [ObservableProperty]
    private ObservableCollection<string> _recent = new();

    /// <summary>The "build a system like X which does Y" goal for the New System path.</summary>
    [ObservableProperty]
    private string _newSystemDescription = string.Empty;

    public WelcomeViewModel(RecentProjectsStore recents, string recentsPath, IFolderPicker picker,
        Action<string> onProjectChosen)
    {
        _recents = recents;
        _recentsPath = recentsPath;
        _picker = picker;
        _onProjectChosen = onProjectChosen;
    }

    public async Task LoadRecentsAsync()
    {
        Recent.Clear();
        foreach (var p in await _recents.LoadAsync(_recentsPath))
            Recent.Add(p);
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
