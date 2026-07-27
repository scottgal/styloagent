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

    [Fact]
    public async Task Send_exposes_thinking_state_until_local_synthesis_completes()
    {
        var pending = new TaskCompletionSource<DocumentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new DocsChatViewModel(_ => pending.Task) { Draft = "Summarise the design" };

        var sending = vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.IsThinking);
        Assert.Contains("synthesizing", vm.Status, StringComparison.OrdinalIgnoreCase);

        pending.SetResult(new DocumentAnswer("Synthesised answer", [], true));
        await sending;

        Assert.False(vm.IsThinking);
        Assert.Equal("Synthesised answer", vm.Messages[^1].Text);
    }
}
