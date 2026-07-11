using CommunityToolkit.Mvvm.ComponentModel;
using Styloagent.Core.Git;

namespace Styloagent.App.ViewModels;

/// <summary>
/// Holds the parsed diff for the file currently selected in <see cref="ChangesViewModel"/>.
/// </summary>
public sealed partial class DiffViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    private FileDiff? _file;

    /// <summary>True when the loaded diff contains at least one line.</summary>
    public bool HasDiff => File is { Lines.Count: > 0 };
}
