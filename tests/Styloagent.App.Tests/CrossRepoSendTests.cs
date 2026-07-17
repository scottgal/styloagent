using Styloagent.App.ViewModels;
using Styloagent.Core.Mcp;

namespace Styloagent.App.Tests;

/// <summary>
/// SendBusMessage's cross-repo resolution (Bug A, co-landed with bus-'s MessageRequest.Repo @30caa0c):
/// a blank repo stays intra-repo (own channel, single-repo byte-identical), and an unknown repo fails
/// loudly rather than silently dropping. Delivering to a LIVE second instance needs a real agent launch,
/// so it's restart-verified — these cover the headless-testable resolution surface.
/// </summary>
public class CrossRepoSendTests
{
    [Fact]
    public async Task SendBusMessage_BlankRepo_SendsToOwnChannel_NoFromRepoStamp()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());

            var outcome = await vm.SendBusMessage(
                new MessageRequest("cockpit-", "overview-", "hello there", "body", "normal"));

            Assert.True(outcome.Sent);                                   // intra-repo → the sender's own channel
            Assert.NotNull(outcome.Path);
            Assert.DoesNotContain("From-Repo", File.ReadAllText(outcome.Path!));   // single-repo output unchanged
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SendBusMessage_UnknownRepo_FailsLoudly_RatherThanSilentlyDropping()
    {
        var root = MainWindowViewModelTests.MakeTwoAgentChannel();
        try
        {
            var vm = await MainWindowViewModel.InitializeAsync(root, new FakeLauncher(), new FakeWatcher());

            var outcome = await vm.SendBusMessage(
                new MessageRequest("cockpit-", "overview-", "q", "b", "normal", Repo: "no-such-repo"));

            Assert.False(outcome.Sent);
            Assert.Contains("unknown repo", outcome.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
