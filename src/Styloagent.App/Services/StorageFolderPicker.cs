using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Styloagent.App.Services;

/// <summary>Real folder picker backed by a window's StorageProvider.</summary>
public sealed class StorageFolderPicker : IFolderPicker
{
    private readonly TopLevel _topLevel;
    public StorageFolderPicker(TopLevel topLevel) => _topLevel = topLevel;

    public async Task<string?> PickFolderAsync()
    {
        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false, Title = "Open a project folder" });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }
}
