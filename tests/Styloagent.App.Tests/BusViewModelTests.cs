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

    // A thread's pill reflects HANDLING status: WAITING (nobody's on it) → WORKING (an agent picked
    // it up) → DONE (replied/archived). It keys off NeedsReply (has an unreplied inbound) + pickup, NOT
    // the section — a SEEN thread is demoted to Recent yet still needs a reply, so it keeps its pill.

    [Theory]
    [InlineData(true,  false, "WAITING")]   // needs a reply, nobody picked it up → loud
    [InlineData(true,  true,  "WORKING")]   // needs a reply, an agent is on it → de-emphasized
    [InlineData(false, false, "")]          // nothing to action (broadcast / in-flight) → no pill
    [InlineData(false, true,  "")]
    public void BusThreadItem_Pill_FromNeedsReplyAndPickup(bool needsReply, bool pickedUp, string pill)
    {
        var t = new BusThreadItem
        {
            Section    = needsReply ? BusThreadSection.Attention : BusThreadSection.Recent,
            NeedsReply = needsReply,
            IsPickedUp = pickedUp,
        };
        Assert.Equal(pill, t.StatusPillText);
        Assert.Equal(pill.Length > 0, t.HasStatusPill);
    }

    [Fact]
    public void BusThreadItem_Working_IsDeemphasizedButNotDone_WithDistinctColor()
    {
        var waiting = new BusThreadItem { Section = BusThreadSection.Attention, NeedsReply = true };
        var working = new BusThreadItem { Section = BusThreadSection.Attention, NeedsReply = true, IsPickedUp = true };
        var done    = new BusThreadItem { Section = BusThreadSection.Archive };
        Assert.Equal("WORKING", working.StatusPillText);
        Assert.False(working.IsDone);
        Assert.True(working.RowOpacity < 1.0 && working.RowOpacity > 0.5);   // between WAITING and DONE
        Assert.NotEqual(waiting.StatusPillFgHex, working.StatusPillFgHex);
        Assert.NotEqual(done.StatusPillFgHex, working.StatusPillFgHex);
    }

    [Fact]
    public void BusThreadItem_ArchivedSection_IsDone()
    {
        var t = new BusThreadItem { Section = BusThreadSection.Archive };
        Assert.True(t.IsDone);
        Assert.Equal("DONE", t.StatusPillText);
        Assert.Equal(0.5, t.RowOpacity);
    }

    [Fact]
    public void BusThreadItem_OperatorArchived_IsDone_SupersedesWorking()
    {
        var t = new BusThreadItem
        {
            Section = BusThreadSection.Attention, NeedsReply = true, IsPickedUp = true, IsOperatorArchived = true,
        };
        Assert.True(t.IsDone);
        Assert.Equal("DONE", t.StatusPillText);
        Assert.False(t.CanArchive);
    }

    [Fact]
    public void BusThreadItem_OperatorArchived_FlipsPillToDone_InPlace()
    {
        // The ✕ archive gesture flips the pill to DONE instantly, before the reload re-sections it.
        var t = new BusThreadItem { Section = BusThreadSection.Attention, NeedsReply = true };
        Assert.Equal("WAITING", t.StatusPillText);
        var changed = new List<string>();
        t.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        t.IsOperatorArchived = true;
        Assert.Equal("DONE", t.StatusPillText);
        Assert.Contains(nameof(BusThreadItem.StatusPillText), changed);
        Assert.Contains(nameof(BusThreadItem.CanArchive), changed);
    }

    [Theory]
    [InlineData("New", false, false, "WAITING")]
    [InlineData("New", true,  false, "WORKING")]   // this message was picked up by its recipient
    [InlineData("New", false, true,  "DONE")]      // operator archived the thread
    [InlineData("Replied", true, false, "DONE")]
    public void BusMessageItem_Pill_FromStatePickupArchive(string state, bool pickedUp, bool archived, string pill)
    {
        var m = new BusMessageItem { State = state, IsPickedUp = pickedUp, IsOperatorArchived = archived };
        Assert.Equal(pill, m.StatusPillText);
        Assert.StartsWith("#", m.StatusPillBgHex);
        Assert.StartsWith("#", m.StatusPillFgHex);
    }

    // ── The fix: operator-seen DEMOTES a thread out of NEEDS ATTENTION; pickup pill; archive → DONE ──

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
    public async Task OpenThread_MarksSeen_AndDemotesOutOfAttention_OnReload()
    {
        var root = AttentionRoot();
        try
        {
            var store = new InMemoryBusViewState();
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(), store);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            var thread = vm.AttentionThreads[0];
            Assert.Equal("WAITING", thread.StatusPillText);      // starts loud, in NEEDS ATTENTION
            vm.OpenThreadCommand.Execute(thread);                // operator views it → SEEN
            Assert.True(store.IsSeen(thread.Key, thread.LastActivity));

            // THE FIX: a seen-but-unreplied thread leaves NEEDS ATTENTION for RECENT — the list shrinks.
            await vm.LoadAsync();
            await WaitUntil(() => vm.RecentThreads.Any(t => t.Key.Contains("open-question")));
            Assert.DoesNotContain(vm.AttentionThreads, t => t.Key.Contains("open-question"));
            Assert.Contains(vm.RecentThreads, t => t.Key.Contains("open-question"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ToggleThread_Expanding_MarksThreadSeen()
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
            Assert.True(thread.IsExpanded);
            Assert.True(store.IsSeen(thread.Key, thread.LastActivity));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ArchiveThread_MarksDone_AndMovesIntoArchive_OnReload()
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

            Assert.True(thread.IsDone);                          // instant → DONE
            Assert.Equal("DONE", thread.StatusPillText);
            Assert.True(store.IsArchived(thread.Key));

            await vm.LoadAsync();
            await WaitUntil(() => vm.ArchivedThreads.Count > 0 && vm.AttentionThreads.Count == 0);
            Assert.Contains(vm.ArchivedThreads, t => t.Subject.Contains("open"));
            Assert.DoesNotContain(vm.AttentionThreads, t => t.Subject.Contains("open"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SeenThread_NewActivity_ReturnsToAttention_AsWaiting_OnReload()
    {
        var root = AttentionRoot();
        try
        {
            var store = new InMemoryBusViewState();
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(), store);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            vm.OpenThreadCommand.Execute(vm.AttentionThreads[0]);   // seen → demoted on reload
            await vm.LoadAsync();
            await WaitUntil(() => vm.RecentThreads.Any(t => t.Key.Contains("open-question")));

            // A fresh follow-up (newer than the seen-watermark) re-demands attention → back to WAITING.
            File.WriteAllText(Path.Combine(root, "inbox", "alpha-follow-up-open-question.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T12:00:00Z\n\nAny update?");
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Any(t => t.Key.Contains("open-question")));
            var back = vm.AttentionThreads.First(t => t.Key.Contains("open-question"));
            Assert.Equal("WAITING", back.StatusPillText);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PickedUpMessage_ShowsWorkingPill()
    {
        var root = AttentionRoot();
        try
        {
            // Pretend the recipient's turn-boundary hook drained this note → picked up (PickupProjection).
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(),
                isPickedUp: (_, _) => true);
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);
            Assert.Equal("WORKING", vm.AttentionThreads[0].StatusPillText);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reload_PreservesExpander()
    {
        var root = AttentionRoot();
        try
        {
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection());
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            vm.AttentionThreads[0].IsExpanded = true;            // expand without marking seen (direct set)

            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Any(t => t.Key.Contains("open-question")));
            var rebuilt = vm.AttentionThreads.First(t => t.Key.Contains("open-question"));
            Assert.True(rebuilt.IsExpanded);                     // reload must not snap it shut
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // ── In-place reconcile (BUG 5): a reload must reuse UNCHANGED row instances so the non-virtualizing
    // ItemsControls keep their containers instead of Clear()+rebuilding every one on every reload. ──

    [Fact]
    public async Task Reload_WithIdenticalData_ReusesEveryThreadInstance()
    {
        var root = AttentionRoot();
        try
        {
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection());
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count > 0);
            var before = vm.AttentionThreads.ToList();
            before[0].IsExpanded = true;                     // operator expands the row

            await vm.LoadAsync();                            // reload, identical data
            await WaitUntil(() => vm.AttentionThreads.Count > 0);

            // No churn: every row instance is REUSED (same reference) and its expand state survives.
            Assert.Equal(before.Count, vm.AttentionThreads.Count);
            for (int i = 0; i < before.Count; i++)
                Assert.Same(before[i], vm.AttentionThreads[i]);
            Assert.True(vm.AttentionThreads[0].IsExpanded);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reload_ReusesUnchangedRow_ButReplacesTheChangedOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "busreconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(root, "outbox"));
        try
        {
            File.WriteAllText(Path.Combine(root, "inbox", "alpha-q1.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T10:00:00Z\n\nQ1?");
            File.WriteAllText(Path.Combine(root, "inbox", "beta-q2.md"),
                "**From:** ops\n**Timestamp:** 2024-01-10T11:00:00Z\n\nQ2?");

            // Only beta's note gets picked up on the 2nd load; alpha's never changes.
            bool betaPicked = false;
            var vm = new BusViewModel(root, Prefixes3, new ChannelProjection(),
                isPickedUp: (_, prefix) => betaPicked && prefix == "beta-");
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Count == 2);

            var alphaBefore = vm.AttentionThreads.First(t => t.Key.Contains("q1"));
            var betaBefore = vm.AttentionThreads.First(t => t.Key.Contains("q2"));
            alphaBefore.IsExpanded = true;
            Assert.Equal("WAITING", betaBefore.StatusPillText);

            betaPicked = true;   // beta flips WAITING→WORKING; alpha is untouched
            await vm.LoadAsync();
            await WaitUntil(() => vm.AttentionThreads.Any(t => t.Key.Contains("q2") && t.StatusPillText == "WORKING"));

            // alpha unchanged → SAME instance reused (container not churned), expand preserved.
            var alphaAfter = vm.AttentionThreads.First(t => t.Key.Contains("q1"));
            Assert.Same(alphaBefore, alphaAfter);
            Assert.True(alphaAfter.IsExpanded);

            // beta changed → REPLACED with a fresh instance now showing WORKING.
            var betaAfter = vm.AttentionThreads.First(t => t.Key.Contains("q2"));
            Assert.NotSame(betaBefore, betaAfter);
            Assert.Equal("WORKING", betaAfter.StatusPillText);
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
