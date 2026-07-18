using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Docs;

namespace Styloagent.App.ViewModels;

/// <summary>How the browser orders a folder's children.</summary>
public enum DocSortMode { NameAsc, NameDesc, DateNewest, DateOldest }

/// <summary>A labelled sort choice for the sort-by control.</summary>
public sealed record DocSortOption(string Label, DocSortMode Mode);

/// <summary>
/// The Document Library side panel as a proper, lazy document-repository browser (operator, 2026-07-18):
/// three collapsed top-level sections (repo / channel / logs) whose contents load ONE folder at a time on
/// first expand — no upfront recursive walk, so "show everything" is instant regardless of the 200+
/// channel/log entries. Adds a sort-by control (name / modified date, asc/desc) and an in-pane
/// filename-search box. The per-directory listing is an <see cref="IDocDirLister"/> seam (App-side default
/// now; swaps to repo-'s Core <c>DocLibraryReader.ListChildren</c> when it lands).
/// </summary>
public sealed partial class DocLibraryViewModel : ObservableObject
{
    /// <summary>
    /// A node in the lazy file/folder tree. A folder (<see cref="IsFolder"/>) starts collapsed with a
    /// single <see cref="IsPlaceholder"/> child (so the expander arrow shows) and loads its real children
    /// on first expand via <see cref="Loader"/>. A file leaf carries its <see cref="Entry"/> and opens.
    /// </summary>
    public sealed partial class DocNode : ObservableObject
    {
        public string Name { get; init; } = string.Empty;
        public bool IsFolder { get; init; }
        public string FullPath { get; init; } = string.Empty;
        public DateTimeOffset LastWriteUtc { get; init; }
        public DocEntry? Entry { get; init; }

        /// <summary>A transient "…" row that gives an unloaded folder its expander arrow; removed on load.</summary>
        public bool IsPlaceholder { get; init; }

        /// <summary>An openable file leaf (not a folder, not the loading placeholder).</summary>
        public bool IsFile => !IsFolder && !IsPlaceholder;

        public ObservableCollection<DocNode> Children { get; } = new();

        /// <summary>Loads this folder's children (off-thread) on first expand; set by the VM for folders.</summary>
        internal Func<DocNode, Task>? Loader;
        internal bool Loaded;

        [ObservableProperty]
        private bool _isExpanded;

        partial void OnIsExpandedChanged(bool value)
        {
            if (value && IsFolder && !Loaded && Loader is not null)
            {
                Loaded = true;
                _ = Loader(this);
            }
        }

        public static DocNode Placeholder() => new() { IsPlaceholder = true, Name = "…" };
    }

    private readonly Action<MarkdownDocumentViewModel> _openDocument;
    private readonly Func<DocEntry, MarkdownDocumentViewModel> _buildDoc;
    private readonly IDocDirLister _lister;
    // Fixed top-level sections (repo / channel / logs) → their root + source. Files inherit their section's
    // source; folders load lazily. Zero I/O in the ctor — the tree is three collapsed rows until expanded.
    private readonly List<(string Root, DocSource Source, string Label)> _sections = new();
    // The UI SynchronizationContext captured at construction, so off-thread loads marshal collection
    // mutations back onto the UI thread; null in a plain unit-test context (→ run inline).
    private readonly SynchronizationContext? _uiContext;

    // A background filename index (names only, NO content reads) so the search box finds files by name
    // across the whole library without a content-index build. Swaps to repo-'s filename index when ready.
    private IReadOnlyList<DocEntry> _fileIndex = Array.Empty<DocEntry>();

    /// <summary>The lazy file/folder tree (bound by the TreeView) — three collapsed sections at the top.</summary>
    public ObservableCollection<DocNode> Roots { get; } = new();

    /// <summary>Flat filename-search results, shown INSTEAD of the tree while <see cref="SearchText"/> is set.</summary>
    public ObservableCollection<DocEntry> SearchResults { get; } = new();

    public IReadOnlyList<DocSortOption> SortModes { get; } = new[]
    {
        new DocSortOption("Name A–Z", DocSortMode.NameAsc),
        new DocSortOption("Name Z–A", DocSortMode.NameDesc),
        new DocSortOption("Newest",   DocSortMode.DateNewest),
        new DocSortOption("Oldest",   DocSortMode.DateOldest),
    };

    [ObservableProperty]
    private DocSortOption _selectedSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSearchResults))]
    private string _searchText = string.Empty;

    /// <summary>Show the flat search results (and hide the tree) while a filename query is entered.</summary>
    public bool ShowSearchResults => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Pass-through for the System Map command on <see cref="MainWindowViewModel"/>.</summary>
    public ICommand? ShowSystemMapCommand { get; init; }

    /// <summary>Pass-through for the Bus Sequence command on <see cref="MainWindowViewModel"/>.</summary>
    public ICommand? ShowBusSequenceCommand { get; init; }

    /// <summary>Pass-through for the live Architecture (C4) command on <see cref="MainWindowViewModel"/>.</summary>
    public ICommand? ShowArchitectureCommand { get; init; }

    public DocLibraryViewModel(
        string? repoRoot,
        string? channelRoot,
        Action<MarkdownDocumentViewModel> openDocument,
        IDocDirLister? lister = null,
        Func<DocEntry, MarkdownDocumentViewModel>? buildDoc = null,
        string? logsRoot = null)
    {
        _openDocument = openDocument;
        _lister = lister ?? new LocalDocDirLister();
        _buildDoc = buildDoc ?? (entry => new MarkdownDocumentViewModel(entry.Title, entry.FullPath));
        _uiContext = SynchronizationContext.Current;
        _selectedSort = SortModes[0];

        AddSection(repoRoot, DocSource.Repo, "repo");
        AddSection(channelRoot, DocSource.Channel, "channel");
        AddSection(logsRoot ?? ResolveLogsRoot(channelRoot), DocSource.Log, "logs");

        BuildTopLevel();
        _ = BuildFileIndexAsync();   // background filename walk (no content) → powers the name search
    }

    private void AddSection(string? root, DocSource source, string label)
    {
        if (!string.IsNullOrWhiteSpace(root))
            _sections.Add((root!, source, label));
    }

    /// <summary>The per-agent log dir: the <c>logs/</c> sibling of the channel root (both under
    /// <c>.styloagent/</c>). Mirrors DocLibraryReader; App-side so the ctor needs no Core call.</summary>
    private static string? ResolveLogsRoot(string? channelRoot)
    {
        if (string.IsNullOrWhiteSpace(channelRoot)) return null;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(channelRoot));
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "logs");
    }

    private void BuildTopLevel()
    {
        Roots.Clear();
        foreach (var (root, _, label) in _sections)
            Roots.Add(MakeFolder(label, root));
    }

    private DocNode MakeFolder(string name, string fullPath, DateTimeOffset mtime = default)
    {
        var node = new DocNode
        {
            Name = name, IsFolder = true, FullPath = fullPath, LastWriteUtc = mtime, IsExpanded = false,
        };
        node.Children.Add(DocNode.Placeholder());   // gives the collapsed folder its expander arrow
        node.Loader = LoadFolderAsync;
        return node;
    }

    /// <summary>Loads <paramref name="folder"/>'s immediate children OFF the UI thread (the lister is the
    /// only I/O), then marshals the sorted nodes back onto the UI thread — so expanding a folder in a big
    /// tree never blocks the render thread.</summary>
    private async Task LoadFolderAsync(DocNode folder)
    {
        IReadOnlyList<DocDirItem> items;
        try { items = await Task.Run(() => _lister.List(folder.FullPath)); }
        catch { items = Array.Empty<DocDirItem>(); }

        var (root, source) = SectionOf(folder.FullPath);
        var children = items.Select(it => it.IsFolder
            ? MakeFolder(it.Name, it.FullPath, it.LastWriteUtc)
            : new DocNode
            {
                Name = it.Name, IsFolder = false, FullPath = it.FullPath, LastWriteUtc = it.LastWriteUtc,
                Entry = ToEntry(it, root, source),
            }).ToList();

        var ordered = Sorted(children).ToList();
        await RunOnUiAsync(() =>
        {
            folder.Children.Clear();   // drop the placeholder
            foreach (var c in ordered) folder.Children.Add(c);
        });
    }

    private (string Root, DocSource Source) SectionOf(string path)
    {
        foreach (var (root, source, _) in _sections)
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return (root, source);
        return (_sections.Count > 0 ? _sections[0].Root : "", DocSource.Repo);
    }

    private static DocEntry ToEntry(DocDirItem item, string root, DocSource source)
    {
        var rel = string.IsNullOrEmpty(root) ? item.Name
            : Path.GetRelativePath(root, item.FullPath).Replace('\\', '/');
        return new DocEntry(item.Name, item.FullPath, source, rel);
    }

    // ── Sort ─────────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<DocNode> Sorted(IEnumerable<DocNode> nodes) => SelectedSort.Mode switch
    {
        DocSortMode.NameDesc =>
            nodes.OrderByDescending(n => n.IsFolder).ThenByDescending(n => n.Name, StringComparer.OrdinalIgnoreCase),
        DocSortMode.DateNewest =>
            nodes.OrderByDescending(n => n.IsFolder).ThenByDescending(n => n.LastWriteUtc),
        DocSortMode.DateOldest =>
            nodes.OrderByDescending(n => n.IsFolder).ThenBy(n => n.LastWriteUtc),
        _ => // NameAsc
            nodes.OrderByDescending(n => n.IsFolder).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
    };

    partial void OnSelectedSortChanged(DocSortOption value)
    {
        foreach (var root in Roots) ResortNode(root);
        ApplySearch();
    }

    private void ResortNode(DocNode node)
    {
        // Not-yet-loaded folders (a lone placeholder) and leaves have nothing to reorder.
        if (node.Children.Count <= 1 && (node.Children.Count == 0 || node.Children[0].IsPlaceholder))
            return;
        var ordered = Sorted(node.Children).ToList();
        node.Children.Clear();
        foreach (var c in ordered) { node.Children.Add(c); ResortNode(c); }
    }

    // ── Filename search ──────────────────────────────────────────────────────────────────────────

    private async Task BuildFileIndexAsync()
    {
        IReadOnlyList<DocEntry> index;
        try { index = await Task.Run(BuildFileIndex); }
        catch { index = Array.Empty<DocEntry>(); }
        _fileIndex = index;
        await RunOnUiAsync(ApplySearch);   // refresh results if a query was already typed
    }

    /// <summary>A cheap, bounded, names-only walk of every section (via the lister) — NO file content is
    /// read, so it stays off the load critical path and powers the by-name search.</summary>
    private IReadOnlyList<DocEntry> BuildFileIndex()
    {
        var files = new List<DocEntry>();
        foreach (var (root, source, _) in _sections)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            int visited = 0;
            while (stack.Count > 0 && visited++ < 20_000)
            {
                foreach (var item in _lister.List(stack.Pop()))
                {
                    if (item.IsFolder) stack.Push(item.FullPath);
                    else files.Add(ToEntry(item, root, source));
                }
            }
        }
        return files;
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        SearchResults.Clear();
        var q = SearchText?.Trim() ?? string.Empty;
        if (q.Length == 0) return;

        var hits = _fileIndex
            .Where(e => e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || e.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Take(200);
        foreach (var h in hits) SearchResults.Add(h);
    }

    // ── Open ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open a document. Building the view-model reads the file (the <see cref="MarkdownDocumentViewModel"/>
    /// ctor) OFF the UI thread; only the built VM marshals back to open it, so selecting never blocks the
    /// render thread. Generates <c>OpenDocCommand</c> (the <c>Async</c> suffix is stripped).
    /// </summary>
    [RelayCommand]
    public async Task OpenDocAsync(DocEntry entry)
    {
        MarkdownDocumentViewModel doc;
        try { doc = await Task.Run(() => _buildDoc(entry)); }
        catch { return; }

        await RunOnUiAsync(() => _openDocument(doc));
    }

    /// <summary>Run <paramref name="action"/> on the UI context captured at construction and await it, so
    /// collection/dock mutations stay on the UI thread. Runs inline when already on that context, or when
    /// none was captured (a plain unit-test thread).</summary>
    private Task RunOnUiAsync(Action action)
    {
        var ctx = _uiContext;
        if (ctx is null || ReferenceEquals(ctx, SynchronizationContext.Current))
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        ctx.Post(_ =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }
}
