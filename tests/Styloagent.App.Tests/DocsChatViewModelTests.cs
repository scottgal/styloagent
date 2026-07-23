using Styloagent.App.ViewModels;
using Styloagent.Core.Retrieval;
using Xunit;

namespace Styloagent.App.Tests;

public sealed class DocsChatViewModelTests
{
    [Fact]
    public async Task Send_adds_grounded_answer_and_sources()
    {
        var source = new ContextHit("docs", "Architecture · Retrieval", "/repo/.styloagent/architecture.md", "document", "RRF retrieval.", .9);
        var vm = new DocsChatViewModel(q => Task.FromResult(new DocumentAnswer("Answer [S1]", [source], true))) { Draft = "How does retrieval work?" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Messages.Count);
        Assert.True(vm.Messages[1].IsUser);
        Assert.Equal("Answer [S1]", vm.Messages[2].Text);
        Assert.Single(vm.Messages[2].Sources!);
    }
}
