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
}
