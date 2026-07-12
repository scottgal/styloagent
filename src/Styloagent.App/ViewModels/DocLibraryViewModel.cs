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

    /// <summary>
    /// A node in the document file/folder tree shown by <see cref="Groups"/>'s tree counterpart
    /// <see cref="Roots"/>. A folder has <see cref="IsFolder"/> true and no <see cref="Entry"/>; a
    /// file leaf carries its <see cref="DocEntry"/> and is what gets opened.
    /// </summary>
    public sealed partial class DocNode : ObservableObject
    {
        public string Name { get; init; } = string.Empty;
        public bool IsFolder { get; init; }
        public DocEntry? Entry { get; init; }
        public ObservableCollection<DocNode> Children { get; } = new();

        // Folders start expanded so the tree reads as a doc outline at a glance.
        [ObservableProperty]
        private bool _isExpanded = true;
    }

    private readonly string? _repoRoot;
    private readonly string? _channelRoot;
    private readonly Action<MarkdownDocumentViewModel> _openDocument;

    public ObservableCollection<DocGroupViewModel> Groups { get; } = new();

    /// <summary>The documents as a per-source file/folder tree (bound by the TreeView).</summary>
    public ObservableCollection<DocNode> Roots { get; } = new();

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

    /// <summary>Pass-through for the live Architecture (C4) command on <see cref="MainWindowViewModel"/>.</summary>
    public ICommand? ShowArchitectureCommand { get; init; }

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
        Roots.Clear();

        // Repo group first, then channel.
        var repoEntries = all.Where(e => e.Source == DocSource.Repo).ToList();
        var channelEntries = all.Where(e => e.Source == DocSource.Channel).ToList();

        if (repoEntries.Count > 0)
        {
            var group = new DocGroupViewModel { Header = "repo" };
            foreach (var entry in repoEntries)
                group.Entries.Add(entry);
            Groups.Add(group);
            Roots.Add(BuildTree("repo", repoEntries));
        }

        if (channelEntries.Count > 0)
        {
            var group = new DocGroupViewModel { Header = "channel" };
            foreach (var entry in channelEntries)
                group.Entries.Add(entry);
            Groups.Add(group);
            Roots.Add(BuildTree("channel", channelEntries));
        }
    }

    /// <summary>Builds a folder/file tree from each entry's <see cref="DocEntry.RelativePath"/>.</summary>
    private static DocNode BuildTree(string label, IEnumerable<DocEntry> entries)
    {
        var root = new DocNode { Name = label, IsFolder = true };
        foreach (var entry in entries)
        {
            var segments = entry.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            for (int i = 0; i < segments.Length; i++)
            {
                if (i == segments.Length - 1)
                {
                    node.Children.Add(new DocNode { Name = segments[i], IsFolder = false, Entry = entry });
                }
                else
                {
                    var folder = node.Children.FirstOrDefault(c => c.IsFolder && c.Name == segments[i]);
                    if (folder is null)
                    {
                        folder = new DocNode { Name = segments[i], IsFolder = true };
                        node.Children.Add(folder);
                    }
                    node = folder;
                }
            }
        }
        SortChildren(root);
        return root;
    }

    /// <summary>Folders before files, each alphabetical (case-insensitive), recursively.</summary>
    private static void SortChildren(DocNode node)
    {
        var ordered = node.Children
            .OrderByDescending(c => c.IsFolder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        foreach (var child in ordered)
        {
            node.Children.Add(child);
            SortChildren(child);
        }
    }

    [RelayCommand]
    public void OpenDoc(DocEntry entry)
    {
        var vm = new MarkdownDocumentViewModel(entry.Title, entry.FullPath);
        _openDocument(vm);
    }
}
