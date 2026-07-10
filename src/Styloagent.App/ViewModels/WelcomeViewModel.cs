using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.App.Config;
using Styloagent.App.Services;

namespace Styloagent.App.ViewModels;

/// <summary>The startup screen: open a project folder, or reopen a recent one.</summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly RecentProjectsStore _recents;
    private readonly string _recentsPath;
    private readonly IFolderPicker _picker;
    private readonly Action<string> _onProjectChosen;

    [ObservableProperty]
    private ObservableCollection<string> _recent = new();

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

    [RelayCommand]
    private void OpenRecent(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _onProjectChosen(path);
    }
}
