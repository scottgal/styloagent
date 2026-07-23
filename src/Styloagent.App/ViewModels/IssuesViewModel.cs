using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Styloagent.Core.Issues;

namespace Styloagent.App.ViewModels;

/// <summary>
/// One filed issue as a roster card. Wraps the Core <see cref="Issue"/> (which stays a plain record)
/// and adds the UI-only expand state, mirroring how a bus thread expands.
/// </summary>
public sealed partial class IssueItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>The human-written detail (frontmatter + title stripped), shown when the card is expanded.</summary>
    public string Detail { get; init; } = "";
    public string Reporter { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Status { get; init; } = "";
    public string Area { get; init; } = "";

    /// <summary>Whether the card is expanded to reveal <see cref="Detail"/>.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

/// <summary>
/// View-model for the Issues panel. Reads the file-drop issues under a project's
/// <c>.styloagent/issues/</c> and exposes the ACTIVE ones (not yet resolved) newest-first, as expandable
/// cards. Resolving a card marks the issue <c>closed</c> and drops it from the active list. Agents file
/// issues over MCP (<c>report_issue</c>); a future GitHub feed will land here too via a triage agent.
/// </summary>
public sealed partial class IssuesViewModel : ObservableObject, IDisposable
{
    private readonly string _issuesDir;
    private readonly List<IssueItem> _allIssues = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _refreshDebounce;

    /// <summary>
    /// Opens a file path as a rendered-markdown dock document — the shared "open as rendered markdown"
    /// gesture wired to <c>MainWindowViewModel.OpenDocumentByPath</c>, so an issue opens exactly like a
    /// bus thread or a doc-library markdown file. Null in tests / when no shell is hosting the panel.
    /// </summary>
    private readonly Action<string>? _openDocument;

    public ObservableCollection<IssueItem> Issues { get; } = new();
    public ObservableCollection<string> Areas { get; } = new(["all"]);
    public IReadOnlyList<string> StatusFilters { get; } = ["open", "closed", "all"];
    public IReadOnlyList<string> SeverityFilters { get; } = ["all", "high", "medium", "low"];

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    [ObservableProperty] private string _statusFilter = "open";
    partial void OnStatusFilterChanged(string value) => ApplyFilters();

    [ObservableProperty] private string _severityFilter = "all";
    partial void OnSeverityFilterChanged(string value) => ApplyFilters();

    [ObservableProperty] private string _areaFilter = "all";
    partial void OnAreaFilterChanged(string value) => ApplyFilters();

    /// <summary>Count of open (unresolved) issues — drives the tab badge.</summary>
    public int OpenCount => _allIssues.Count(i => i.Status == "open");
    public int TotalCount => _allIssues.Count;
    public string FilterSummary => $"{Issues.Count} of {TotalCount}";

    /// <summary>Empty-state visibility for the view.</summary>
    public bool HasIssues => Issues.Count > 0;

    public IssuesViewModel(string issuesDir, Action<string>? openDocument = null)
    {
        _issuesDir = issuesDir;
        _openDocument = openDocument;
        _refreshDebounce = new Timer(_ => Dispatcher.UIThread.Post(Refresh), null,
            Timeout.Infinite, Timeout.Infinite);
        try { Directory.CreateDirectory(_issuesDir); } catch { }
        Refresh();
        if (Directory.Exists(_issuesDir))
        {
            _watcher = new FileSystemWatcher(_issuesDir, "*.md")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnIssueChanged;
            _watcher.Changed += OnIssueChanged;
            _watcher.Deleted += OnIssueChanged;
            _watcher.Renamed += OnIssueChanged;
        }
    }

    /// <summary>
    /// Opens the issue's own <c>.md</c> file in the rendered-markdown viewer (a new dock document) — the
    /// common "open as rendered markdown" action every markdown-backed surface offers. The file lives at
    /// <c>&lt;issuesDir&gt;/&lt;id&gt;.md</c> (the same convention <see cref="IssueStore"/> reads/resolves by).
    /// </summary>
    [RelayCommand]
    private void OpenAsMarkdown(IssueItem? item)
    {
        if (item is null || string.IsNullOrEmpty(item.Id)) return;
        _openDocument?.Invoke(Path.Combine(_issuesDir, item.Id + ".md"));
    }

    [RelayCommand]
    public void Refresh()
    {
        _allIssues.Clear();
        foreach (var issue in IssueStore.Read(_issuesDir))
        {
            _allIssues.Add(new IssueItem
            {
                Id         = issue.Id,
                Title      = issue.Title,
                Detail     = CleanDetail(issue.Detail),
                Reporter   = issue.Reporter,
                Severity   = issue.Severity,
                Status     = issue.Status,
                Area       = issue.Reporter,
            });
        }
        Areas.Clear();
        Areas.Add("all");
        foreach (var area in _allIssues.Select(i => i.Area).Distinct(StringComparer.OrdinalIgnoreCase).Order())
            Areas.Add(area);
        if (!Areas.Contains(AreaFilter)) AreaFilter = "all";
        ApplyFilters();
        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(TotalCount));
    }

    /// <summary>Marks a handled issue resolved (closed) and drops it from the active list.</summary>
    [RelayCommand]
    private void Resolve(IssueItem? item)
    {
        if (item is null) return;
        IssueStore.Resolve(_issuesDir, item.Id);
        Refresh();
    }

    private void ApplyFilters()
    {
        var query = SearchText.Trim();
        var desired = _allIssues.Where(i =>
            (StatusFilter == "all" || string.Equals(i.Status, StatusFilter, StringComparison.OrdinalIgnoreCase))
            && (SeverityFilter == "all" || string.Equals(i.Severity, SeverityFilter, StringComparison.OrdinalIgnoreCase))
            && (AreaFilter == "all" || string.Equals(i.Area, AreaFilter, StringComparison.OrdinalIgnoreCase))
            && (query.Length == 0
                || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || i.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)
                || i.Reporter.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Issues.Clear();
        foreach (var item in desired) Issues.Add(item);
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(FilterSummary));
    }

    private void OnIssueChanged(object sender, FileSystemEventArgs e) =>
        _refreshDebounce.Change(150, Timeout.Infinite);

    public void Dispose()
    {
        _watcher?.Dispose();
        _refreshDebounce.Dispose();
    }

    private static readonly Regex TitleLine = new(@"^#\s+.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Strips the file's <c>**From/Timestamp/Severity/Status/Source**</c> frontmatter and the <c># Title</c>
    /// heading, leaving just the reporter's detail text for the expanded card. Falls back to the raw body.
    /// </summary>
    private static string CleanDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var m = TitleLine.Match(body);
        return m.Success ? body[(m.Index + m.Length)..].Trim() : body.Trim();
    }
}
