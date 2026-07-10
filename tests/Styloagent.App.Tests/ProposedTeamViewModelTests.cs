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

    [Fact]
    public void SpawnAll_invokes_callback_once_per_proposal_and_clears_collection()
    {
        var path = Path.Combine(Path.GetTempPath(), "pt-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path,
            "agents:\n" +
            "  - prefix: alpha-\n    responsibility: frontend\n    dir: .\n    launchPrompt: go\n" +
            "  - prefix: beta-\n    responsibility: backend\n    dir: .\n    launchPrompt: run\n");
        try
        {
            var spawned = new List<ProposedAgent>();
            var vm = new ProposedTeamViewModel(path, a => spawned.Add(a));
            vm.Refresh();

            Assert.Equal(2, vm.Proposals.Count);

            vm.SpawnAllCommand.Execute(null);

            Assert.Equal(2, spawned.Count);
            Assert.Contains(spawned, a => a.Prefix == "alpha-");
            Assert.Contains(spawned, a => a.Prefix == "beta-");
            Assert.Empty(vm.Proposals);
        }
        finally { File.Delete(path); }
    }
}
