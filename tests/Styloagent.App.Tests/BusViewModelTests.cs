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

    // ── 2-state status pills + fade (signal-bus-viewer-fadecollapse-completed-message) ──────────

    [Theory]
    [InlineData("New", false, "WAITING")]
    [InlineData("Replied", true, "DONE")]
    [InlineData("Archived", true, "DONE")]
    public void BusMessageItem_MapsState_ToPillAndFade(string state, bool done, string pill)
    {
        var m = new BusMessageItem { State = state };
        Assert.Equal(done, m.IsDone);
        Assert.Equal(pill, m.StatusPillText);
        Assert.Equal(done ? 0.5 : 1.0, m.RowOpacity);      // DONE fades out
        Assert.StartsWith("#", m.StatusPillBgHex);
        Assert.StartsWith("#", m.StatusPillFgHex);
    }

    [Theory]
    [InlineData(BusThreadSection.Attention, "WAITING", true, 1.0)]
    [InlineData(BusThreadSection.Archive, "DONE", true, 0.5)]   // handled → faded, auto-collapsed into Archive
    [InlineData(BusThreadSection.Recent, "", false, 1.0)]        // in-flight, no pill
    public void BusThreadItem_MapsSection_ToPillAndFade(
        BusThreadSection section, string pill, bool hasPill, double opacity)
    {
        var t = new BusThreadItem { Section = section };
        Assert.Equal(pill, t.StatusPillText);
        Assert.Equal(hasPill, t.HasStatusPill);
        Assert.Equal(section == BusThreadSection.Archive, t.IsDone);
        Assert.Equal(opacity, t.RowOpacity);
    }

    // ── 3-state upgrade: SEEN is the middle rung (operator viewed but not yet replied/archived) ──────

    [Fact]
    public void BusThreadItem_Attention_Seen_ShowsSeenPill_BetweenWaitingAndDone()
    {
        var t = new BusThreadItem { Section = BusThreadSection.Attention, IsSeen = true };
        Assert.Equal("SEEN", t.StatusPillText);
        Assert.True(t.HasStatusPill);
        Assert.True(t.IsSeenState);
        Assert.False(t.IsWaiting);   // no longer loud
        Assert.False(t.IsDone);      // still needs a reply
        // De-emphasized but NOT as faded as DONE — it still awaits a reply.
        Assert.True(t.RowOpacity < 1.0 && t.RowOpacity > 0.5);
    }

    [Fact]
    public void BusThreadItem_SeenPill_ColorsAreDistinctFromWaitingAndDone()
    {
        var waiting = new BusThreadItem { Section = BusThreadSection.Attention };
        var seen    = new BusThreadItem { Section = BusThreadSection.Attention, IsSeen = true };
        var done    = new BusThreadItem { Section = BusThreadSection.Archive };
        Assert.NotEqual(waiting.StatusPillFgHex, seen.StatusPillFgHex);
        Assert.NotEqual(done.StatusPillFgHex, seen.StatusPillFgHex);
        Assert.StartsWith("#", seen.StatusPillBgHex);
        Assert.StartsWith("#", seen.StatusPillFgHex);
    }

    [Fact]
    public void BusThreadItem_OperatorArchived_IsDone_EvenIfStillInAttention()
    {
        // Operator explicitly dismissed an unreplied thread → DONE, regardless of content section.
        var t = new BusThreadItem { Section = BusThreadSection.Attention, IsOperatorArchived = true };
        Assert.True(t.IsDone);
        Assert.Equal("DONE", t.StatusPillText);
        Assert.Equal(0.5, t.RowOpacity);
    }

    [Fact]
    public void BusThreadItem_SeenIsIgnored_OnceDone()
    {
        // A replied/archived thread is DONE; a stale seen flag must not downgrade it to SEEN.
        var t = new BusThreadItem { Section = BusThreadSection.Archive, IsSeen = true };
        Assert.True(t.IsDone);
        Assert.Equal("DONE", t.StatusPillText);
    }

    [Fact]
    public void BusThreadItem_PillProps_RaiseChange_WhenSeenFlips()
    {
        // Marking a live thread SEEN must update the pill in place (no reload) — so the derived
        // pill props raise PropertyChanged when IsSeen flips.
        var t = new BusThreadItem { Section = BusThreadSection.Attention };
        var changed = new List<string>();
        t.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        t.IsSeen = true;
        Assert.Contains(nameof(BusThreadItem.StatusPillText), changed);
        Assert.Contains(nameof(BusThreadItem.RowOpacity), changed);
    }

    [Theory]
    [InlineData("New", false, false, "WAITING")]
    [InlineData("New", true,  false, "SEEN")]     // operator viewed the thread this message belongs to
    [InlineData("New", false, true,  "DONE")]     // operator archived it
    [InlineData("Replied", true, false, "DONE")]  // seen is irrelevant once handled
    public void BusMessageItem_MapsState_Seen_Archived_ToPill(
        string state, bool seen, bool archived, string pill)
    {
        var m = new BusMessageItem { State = state, IsSeen = seen, IsOperatorArchived = archived };
        Assert.Equal(pill, m.StatusPillText);
        Assert.StartsWith("#", m.StatusPillBgHex);
        Assert.StartsWith("#", m.StatusPillFgHex);
    }

    // ── 3-state gestures: mark-seen on view, explicit archive, live re-seed on reload ────────────

    /// <summary>A single unreplied inbound so the one thread lands in Attention/WAITING.</summary>
    private static string AttentionRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "busseen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(root, "outbox"));
        File.WriteAllText(Path.Combine(root, "inbox", "alpha-open-question.md"),
            "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ?");
        return root;
    }

    [Fact]
    public async Task ToggleThread_Expanding_MarksThreadSeen_InPlaceAndInStore()
    {
        var root = AttentionRoot();
        try
        {
            var store = new InMemoryBusViewState();
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(), store);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            var thread = vm.AttentionThreads[0];
            Assert.Equal("WAITING", thread.StatusPillText);      // starts loud

            vm.ToggleThreadCommand.Execute(thread);              // operator opens it

            Assert.True(thread.IsExpanded);
            Assert.Equal("SEEN", thread.StatusPillText);         // in-place pill update
            Assert.True(store.IsSeen(thread.Key, thread.LastActivity));   // persisted
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OpenThread_MarksThreadSeen()
    {
        var root = AttentionRoot();
        try
        {
            var store = new InMemoryBusViewState();
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(), store);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            var thread = vm.AttentionThreads[0];
            vm.OpenThreadCommand.Execute(thread);

            Assert.Equal("SEEN", thread.StatusPillText);
            Assert.True(store.IsSeen(thread.Key, thread.LastActivity));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ArchiveThread_MarksDone_AndReSectionsIntoArchive_OnReload()
    {
        var root = AttentionRoot();
        try
        {
            var store = new InMemoryBusViewState();
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(), store);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            var thread = vm.AttentionThreads[0];
            vm.ArchiveThreadCommand.Execute(thread);

            Assert.True(thread.IsDone);                          // in-place → DONE immediately
            Assert.Equal("DONE", thread.StatusPillText);
            Assert.True(store.IsArchived(thread.Key));           // persisted

            // A reload (FSW path) re-sections the operator-archived thread into the Archive drawer,
            // even though its content is still an unreplied inbound (classifier says Attention).
            await vm.LoadAsync();
            await WaitUntil(() => vm.ArchivedThreads.Count > 0 && vm.AttentionThreads.Count == 0);
            Assert.Contains(vm.ArchivedThreads, t => t.Subject.Contains("open"));
            Assert.DoesNotContain(vm.AttentionThreads, t => t.Subject.Contains("open"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reload_ReSeedsSeenFromStore_AndPreservesExpander()
    {
        var root = AttentionRoot();
        try
        {
            var store = new InMemoryBusViewState();
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(), store);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            var thread = vm.AttentionThreads[0];
            vm.ToggleThreadCommand.Execute(thread);              // expand + mark seen
            Assert.True(thread.IsExpanded);

            // Simulate an FSW-driven reload — the rebuilt row must stay SEEN (from store) and expanded.
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);
            var rebuilt = vm.AttentionThreads[0];
            Assert.Equal("SEEN", rebuilt.StatusPillText);
            Assert.True(rebuilt.IsExpanded);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NewActivity_AfterSeen_RevertsThreadToWaiting_OnReload()
    {
        var root = AttentionRoot();
        try
        {
            var store = new InMemoryBusViewState();
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(), store);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            var thread = vm.AttentionThreads[0];
            vm.ToggleThreadCommand.Execute(thread);
            Assert.Equal("SEEN", thread.StatusPillText);

            // A fresh follow-up on the SAME thread (newer than the seen-watermark) re-demands attention.
            File.WriteAllText(Path.Combine(root, "inbox", "alpha-follow-up-open-question.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T12:00:00Z\n\nAny update?");
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);
            var rebuilt = vm.AttentionThreads.First(t => t.Key.Contains("open-question"));
            Assert.Equal("WAITING", rebuilt.StatusPillText);     // un-seen by new activity
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
