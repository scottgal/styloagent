using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Docs;

namespace Styloagent.App.ViewModels;

/// <summary>
/// View-model for the Document Library side panel.
/// Groups repo and channel markdown documents into <see cref="Groups"/>, each keyed by
/// a lowercase source label ("repo" or "channel"). Repo group is listed first.
/// </summary>
public sealed partial class DocLibraryViewModel : ObservableObject
{
    /// <summary>A labelled collection of document entries (one per source).</summary>
    public sealed class DocGroupViewModel
    {
        public string Header { get; init; } = string.Empty;
        public ObservableCollection<DocEntry> Entries { get; } = new();
    }

    private readonly string? _repoRoot;
    private readonly string? _channelRoot;
    private readonly Action<MarkdownDocumentViewModel> _openDocument;

    public ObservableCollection<DocGroupViewModel> Groups { get; } = new();

    /// <summary>
    /// Pass-through for the System Map command on <see cref="MainWindowViewModel"/>.
    /// Set by the owner before the view binds, so the DocLibraryView header can reach it
    /// without a RelativeSource walk up to the window.
    /// </summary>
    public ICommand? ShowSystemMapCommand { get; init; }

    /// <summary>
    /// Pass-through for the Bus Sequence command on <see cref="MainWindowViewModel"/>.
    /// Set by the owner before the view binds, so the DocLibraryView header can reach it
    /// without a RelativeSource walk up to the window.
    /// </summary>
    public ICommand? ShowBusSequenceCommand { get; init; }

    public DocLibraryViewModel(
        string? repoRoot,
        string? channelRoot,
        Action<MarkdownDocumentViewModel> openDocument)
    {
        _repoRoot = repoRoot;
        _channelRoot = channelRoot;
        _openDocument = openDocument;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        var all = DocLibraryReader.Read(_repoRoot, _channelRoot);

        Groups.Clear();

        // Repo group first, then channel.
        var repoEntries = all.Where(e => e.Source == DocSource.Repo).ToList();
        var channelEntries = all.Where(e => e.Source == DocSource.Channel).ToList();

        if (repoEntries.Count > 0)
        {
            var group = new DocGroupViewModel { Header = "repo" };
            foreach (var entry in repoEntries)
                group.Entries.Add(entry);
            Groups.Add(group);
        }

        if (channelEntries.Count > 0)
        {
            var group = new DocGroupViewModel { Header = "channel" };
            foreach (var entry in channelEntries)
                group.Entries.Add(entry);
            Groups.Add(group);
        }
    }

    [RelayCommand]
    public void OpenDoc(DocEntry entry)
    {
        var vm = new MarkdownDocumentViewModel(entry.Title, entry.FullPath);
        _openDocument(vm);
    }
}
