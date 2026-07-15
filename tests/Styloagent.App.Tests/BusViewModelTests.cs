using Styloagent.App.ViewModels;
using Styloagent.Core.Channel;

namespace Styloagent.App.Tests;

/// <summary>
/// Asserts BusViewModel populates its Messages collection from a fixture channel dir.
/// Uses LoadAsync directly (not FSW) for determinism.
/// </summary>
public class BusViewModelTests : IDisposable
{
    private static readonly string[] Prefixes3 = { "alpha-", "beta-", "gamma-" };
    private static readonly string[] Prefix1 = { "alpha-" };
    private readonly string _channelRoot;

    /// <summary>
    /// Polls until <paramref name="condition"/> holds or the timeout elapses. BusViewModel updates its
    /// collections via Dispatcher.UIThread.Post, so they are populated shortly AFTER LoadAsync returns —
    /// a fixed delay races under parallel test load; this waits for the actual condition instead.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        for (int waited = 0; waited < timeoutMs && !condition(); waited += 10)
            await Task.Delay(10);
    }

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

        // Messages is updated via Dispatcher.UIThread.Post — wait for the condition, not a fixed delay.
        await WaitUntil(() => vm.Messages.Count > 0);

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
        await WaitUntil(() => vm.Messages.Count > 0);

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
        var vm = new BusViewModel(_channelRoot, Prefix1, new ChannelProjection());
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task LoadAsync_IsSingleFlighted_ConcurrentCallsDoNotOverlap()
    {
        // Fire many concurrent LoadAsync calls; the gate must not throw and the
        // final Messages collection must be valid (non-empty, well-formed items).
        var prefixes = new[] { "alpha-", "beta-" };
        var vm = new BusViewModel(_channelRoot, prefixes, new ChannelProjection());

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => vm.LoadAsync())
            .ToArray();

        await Task.WhenAll(tasks);
        await Task.Delay(50);

        // All tasks completed without exception and collection is consistent.
        Assert.NotEmpty(vm.Messages);
        Assert.All(vm.Messages, item => Assert.NotEmpty(item.Slug));
    }

    [Fact]
    public async Task Dispose_AfterTriggeredReload_DoesNotThrow()
    {
        // Trigger a reload, then dispose immediately — the in-flight callback must no-op.
        var prefixes = new[] { "alpha-", "beta-" };
        var vm = new BusViewModel(_channelRoot, prefixes, new ChannelProjection());

        var loadTask = vm.LoadAsync();

        // Dispose while the load is potentially in flight.
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);

        // Awaiting the load after dispose must also not throw.
        var loadEx = await Record.ExceptionAsync(async () => await loadTask);
        Assert.Null(loadEx);
    }

    [Fact]
    public async Task LoadAsync_BucketsThreads_IntoAttentionRecentArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "busbucket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(root, "outbox"));
        Directory.CreateDirectory(Path.Combine(root, "archive", "inbox"));
        try
        {
            // alpha: unreplied inbox -> Attention
            File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");
            // broadcast: informational, no reply expected -> Recent
            File.WriteAllText(Path.Combine(root, "inbox", "all-heads-up.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T10:30:00Z\n\nFYI.");
            // beta: inbox + reply -> handled, so it must LEAVE the active groups -> Archive
            File.WriteAllText(Path.Combine(root, "inbox", "beta-done-task.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T11:00:00Z\n\nTask.");
            File.WriteAllText(Path.Combine(root, "outbox", "beta-done-task.reply.md"),
                "**From:** beta-\n**Timestamp:** 2024-01-10T11:05:00Z\n\nDone.");
            // gamma: archived -> Archive
            File.WriteAllText(Path.Combine(root, "archive", "inbox", "gamma-old-thing.md"),
                "**From:** ops\n**Timestamp:** 2024-01-09T09:00:00Z\n\nOld.");

            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection());
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0 && vm.RecentThreads.Count > 0 && vm.ArchivedThreads.Count > 1);

            // Attention: the unreplied inbound.
            Assert.Contains(vm.AttentionThreads, t => t.Subject.Contains("open"));
            Assert.All(vm.AttentionThreads, t => Assert.Equal("●", t.Glyph));
            // Recent: the broadcast (nothing to action, not handled).
            Assert.Contains(vm.RecentThreads, t => t.Subject.Contains("heads"));
            // A replied thread must NOT linger in the active groups...
            Assert.DoesNotContain(vm.AttentionThreads, t => t.Subject.Contains("done"));
            Assert.DoesNotContain(vm.RecentThreads, t => t.Subject.Contains("done"));
            // ...it moves to Archive, alongside the plainly-archived thread.
            Assert.Contains(vm.ArchivedThreads, t => t.Subject.Contains("done"));
            Assert.Contains(vm.ArchivedThreads, t => t.Subject.Contains("old"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
