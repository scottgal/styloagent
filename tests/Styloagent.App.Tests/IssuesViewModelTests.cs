using Styloagent.App.ViewModels;
using Styloagent.Core.Issues;
using Xunit;

namespace Styloagent.App.Tests;

public class IssuesViewModelTests
{
    private static string NewDir() => Path.Combine(Path.GetTempPath(), "issvm-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_removes_the_issue_from_the_active_list_and_persists_closed()
    {
        var dir = NewDir();
        try
        {
            IssueStore.Write(dir, "a-", "First", "d1", "high", DateTimeOffset.UtcNow);
            IssueStore.Write(dir, "b-", "Second", "d2", "low", DateTimeOffset.UtcNow);
            var vm = new IssuesViewModel(dir);
            Assert.Equal(2, vm.Issues.Count);
            Assert.Equal(2, vm.OpenCount);

            var first = vm.Issues.First(i => i.Title == "First");
            vm.ResolveCommand.Execute(first);

            Assert.DoesNotContain(vm.Issues, i => i.Title == "First");   // left the active list
            Assert.Single(vm.Issues);
            Assert.Equal(1, vm.OpenCount);

            // Persisted as closed → a fresh reload still excludes it (doesn't come back).
            vm.Refresh();
            Assert.Single(vm.Issues);
            Assert.DoesNotContain(vm.Issues, i => i.Title == "First");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OpenAsMarkdown_invokes_the_open_callback_with_the_issue_file_path()
    {
        var dir = NewDir();
        try
        {
            var issue = IssueStore.Write(dir, "a-", "A bug", "the detail body", "high", DateTimeOffset.UtcNow);
            string? opened = null;
            var vm = new IssuesViewModel(dir, path => opened = path);
            var item = vm.Issues.Single();

            vm.OpenAsMarkdownCommand.Execute(item);

            // Opens the issue's own .md file through the shared open-as-rendered-markdown gesture.
            Assert.Equal(Path.Combine(dir, issue.Id + ".md"), opened);
            Assert.True(File.Exists(opened));   // the file the rendered-markdown viewer will read
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OpenAsMarkdown_isNoOp_forNullItem()
    {
        var dir = NewDir();
        try
        {
            int calls = 0;
            var vm = new IssuesViewModel(dir, _ => calls++);
            vm.OpenAsMarkdownCommand.Execute(null);
            Assert.Equal(0, calls);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Item_expands_to_reveal_its_detail()
    {
        var dir = NewDir();
        try
        {
            IssueStore.Write(dir, "a-", "Title", "the full detail body", "medium", DateTimeOffset.UtcNow);
            var vm = new IssuesViewModel(dir);
            var item = vm.Issues.Single();

            Assert.False(item.IsExpanded);
            Assert.Contains("the full detail body", item.Detail);   // detail available to show
            Assert.Equal("medium", item.Severity);

            item.ToggleExpandCommand.Execute(null);
            Assert.True(item.IsExpanded);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Filters_by_text_status_severity_and_area()
    {
        var dir = NewDir();
        try
        {
            var high = IssueStore.Write(dir, "router-", "SSH lockout", "staging login failed", "high", DateTimeOffset.UtcNow);
            IssueStore.Write(dir, "docs-", "Typo", "manual wording", "low", DateTimeOffset.UtcNow.AddMinutes(-1));
            IssueStore.Resolve(dir, high.Id);
            var vm = new IssuesViewModel(dir);

            vm.StatusFilter = "all";
            Assert.Equal(2, vm.Issues.Count);
            vm.AreaFilter = "router-";
            Assert.Single(vm.Issues);
            vm.AreaFilter = "all";
            vm.SeverityFilter = "low";
            Assert.Single(vm.Issues);
            vm.SeverityFilter = "all";
            vm.SearchText = "staging";
            Assert.Single(vm.Issues);
            Assert.Equal("SSH lockout", vm.Issues[0].Title);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
