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
}
