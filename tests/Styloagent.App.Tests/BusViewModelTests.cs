using Styloagent.App.ViewModels;
using Styloagent.Core.Channel;

namespace Styloagent.App.Tests;

/// <summary>
/// Asserts BusViewModel populates its Messages collection from a fixture channel dir.
/// Uses LoadAsync directly (not FSW) for determinism.
/// </summary>
public class BusViewModelTests : IDisposable
{
    private readonly string _channelRoot;

    public BusViewModelTests()
    {
        _channelRoot = Path.Combine(Path.GetTempPath(), "busvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_channelRoot, "inbox"));
        Directory.CreateDirectory(Path.Combine(_channelRoot, "outbox"));

        // inbox: two messages from two prefixes
        File.WriteAllText(
            Path.Combine(_channelRoot, "inbox", "alpha-hello-world.md"),
            "**From:** orchestrator\n**Timestamp:** 2024-01-10T10:00:00Z\n\nHello from alpha.");

        File.WriteAllText(
            Path.Combine(_channelRoot, "inbox", "beta-task-one.md"),
            "**From:** planner\n**Timestamp:** 2024-01-10T11:00:00Z\n\nTask one for beta.");

        // outbox: a reply from alpha
        File.WriteAllText(
            Path.Combine(_channelRoot, "outbox", "alpha-hello-world.reply.md"),
            "**From:** alpha-\n**Timestamp:** 2024-01-10T10:05:00Z\n\nHello back.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_channelRoot))
            Directory.Delete(_channelRoot, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_PopulatesMessages_WithExpectedRoutingPrefixes()
    {
        var prefixes = new[] { "alpha-", "beta-" };
        var vm = new BusViewModel(_channelRoot, prefixes, new ChannelProjection());

        // Call LoadAsync directly — deterministic, no FSW timing dependency
        await vm.LoadAsync();

        // Wait for UI thread dispatch (Messages is updated via Dispatcher.UIThread.Post)
        // In headless/test context, UIThread post executes synchronously when no dispatcher loop.
        // Give it a brief yield just in case.
        await Task.Delay(50);

        Assert.NotEmpty(vm.Messages);

        var prefixSet = vm.Messages.Select(m => m.RoutingPrefix).Distinct().ToHashSet();
        Assert.Contains("alpha-", prefixSet);
        Assert.Contains("beta-", prefixSet);
    }

    [Fact]
    public async Task LoadAsync_EachItem_HasNonEmptyColorHex()
    {
        var prefixes = new[] { "alpha-", "beta-" };
        var vm = new BusViewModel(_channelRoot, prefixes, new ChannelProjection());
        await vm.LoadAsync();
        await Task.Delay(50);

        Assert.All(vm.Messages, item =>
        {
            Assert.NotEmpty(item.ColorHex);
            Assert.StartsWith("#", item.ColorHex);
        });
    }

    [Fact]
    public async Task LoadAsync_MissingChannelDir_ProducesEmptyFeed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-channel-" + Guid.NewGuid());
        var vm = new BusViewModel(missing, Array.Empty<string>(), new ChannelProjection());
        await vm.LoadAsync();
        await Task.Delay(50);

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var vm = new BusViewModel(_channelRoot, new[] { "alpha-" }, new ChannelProjection());
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }
}
