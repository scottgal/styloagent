using Styloagent.App.Config;
using Styloagent.App.Services;
using Styloagent.App.ViewModels;
using Xunit;

namespace Styloagent.App.Tests;

public class WelcomeViewModelTests
{
    private sealed class FakePicker : IFolderPicker
    {
        private readonly string? _result;
        public FakePicker(string? result) => _result = result;
        public Task<string?> PickFolderAsync() => Task.FromResult(_result);
    }

    [Fact]
    public async Task OpenFolder_raises_onProjectChosen_with_the_picked_path()
    {
        string? chosen = null;
        var recentsPath = Path.Combine(Path.GetTempPath(), "wr-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), recentsPath,
                new FakePicker("/picked/project"), p => chosen = p);

            await vm.OpenFolderCommand.ExecuteAsync(null);

            Assert.Equal("/picked/project", chosen);
        }
        finally { if (File.Exists(recentsPath)) File.Delete(recentsPath); }
    }

    [Fact]
    public void OpenRecent_raises_onProjectChosen()
    {
        string? chosen = null;
        var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
            new FakePicker(null), p => chosen = p);

        vm.OpenRecentCommand.Execute("/recent/proj");

        Assert.Equal("/recent/proj", chosen);
    }

    [Fact]
    public async Task NewSystem_scaffolds_the_folder_writes_the_brief_and_opens_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "newsys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? chosen = null;
        try
        {
            var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
                new FakePicker(root), p => chosen = p)
            {
                NewSystemDescription = "a system like Trello which manages kanban boards",
            };

            await vm.NewSystemCommand.ExecuteAsync(null);

            var briefPath = Path.Combine(root, ".styloagent", "brief.md");
            Assert.True(File.Exists(briefPath));
            var brief = await File.ReadAllTextAsync(briefPath);
            Assert.Contains("Trello", brief);
            Assert.Contains("clarifying questions", brief, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(root, chosen);           // opened the new project
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NewSystem_with_blank_description_does_nothing()
    {
        string? chosen = null;
        var vm = new WelcomeViewModel(new RecentProjectsStore(), "/tmp/none.yaml",
            new FakePicker("/should/not/be/used"), p => chosen = p);

        await vm.NewSystemCommand.ExecuteAsync(null);

        Assert.Null(chosen);
    }
}
