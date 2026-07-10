using Styloagent.App.ViewModels;
using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.App.Tests;

public class ProposedTeamViewModelTests
{
    [Fact]
    public void Refresh_loads_cards_and_Spawn_invokes_callback()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n  - prefix: foss-\n    responsibility: packages\n    dir: .\n    launchPrompt: hi\n");
        try
        {
            ProposedAgent? spawned = null;
            var vm = new ProposedTeamViewModel(path, a => spawned = a);
            vm.Refresh();

            Assert.Single(vm.Proposals);
            Assert.Equal("foss-", vm.Proposals[0].Prefix);
            Assert.Equal("packages", vm.Proposals[0].Responsibility);

            vm.SpawnCommand.Execute(vm.Proposals[0].Agent);
            Assert.NotNull(spawned);
            Assert.Equal("foss-", spawned!.Prefix);
        }
        finally { File.Delete(path); }
    }
}
