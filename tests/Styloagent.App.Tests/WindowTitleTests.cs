using Styloagent.App.ViewModels;
using Styloagent.Core.Workspace;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Operator: show the CURRENT PROJECT NAME in the cockpit title so it's obvious which project this
/// cockpit is focused on (and so multiple open cockpits are distinguishable). <see cref="MainWindowViewModel"/>
/// exposes ProjectName + WindowTitle, driven by the repo registry.
/// </summary>
public class WindowTitleTests
{
    [Fact]
    public async Task WindowTitle_shows_the_primary_project_name()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(channel, new FakeLauncher(), new FakeWatcher());

            var ws = WorkspaceConfig.For("/ws", "mono", new[]
            {
                Path.Combine("/ws", "Styloagent"),      // primary (anchor)
                Path.Combine("/ws", "lucidRESUME"),
            });

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
            vm.SetReposFromOverviews(ws.RepoOverviews());

            Assert.Equal("Styloagent", vm.ProjectName);                        // the primary repo, not the extra
            Assert.Equal("Styloagent — Styloagent Cockpit", vm.WindowTitle);   // project name leads the OS title
            Assert.Contains(nameof(MainWindowViewModel.ProjectName), changed); // title refreshes when repos change
            Assert.Contains(nameof(MainWindowViewModel.WindowTitle), changed);
        }
        finally { if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true); }
    }

    [Fact]
    public async Task WindowTitle_falls_back_to_the_repo_root_folder_name_before_repos_are_set()
    {
        var channel = MainWindowViewModelTests.MakeTwoAgentChannel();
        var repoDir = Path.Combine(Path.GetTempPath(), "MyCoolProject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDir);
        try
        {
            // repoRoot given, SetReposFromOverviews NOT called → derive the name from the root folder.
            var vm = await MainWindowViewModel.InitializeAsync(
                channel, new FakeLauncher(), new FakeWatcher(), repoRoot: repoDir);

            Assert.StartsWith("MyCoolProject-", vm.ProjectName);
            Assert.EndsWith(" — Styloagent Cockpit", vm.WindowTitle);
        }
        finally
        {
            if (Directory.Exists(channel)) Directory.Delete(channel, recursive: true);
            if (Directory.Exists(repoDir)) Directory.Delete(repoDir, recursive: true);
        }
    }
}
