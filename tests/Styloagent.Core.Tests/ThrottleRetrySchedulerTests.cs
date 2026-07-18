using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.Core.Tests;

/// <summary>
/// <see cref="ThrottleRetryScheduler"/> drives the auto-recovery: when an agent stays throttled it posts a
/// retry nudge with exponential backoff, bounded to a cap, and cancels the moment the agent resumes. The
/// backoff delay is injected so these tests are deterministic (no wall-clock waits).
/// </summary>
public class ThrottleRetrySchedulerTests
{
    // Delay that completes immediately — the sequence runs straight through its backoffs.
    private static readonly Func<TimeSpan, CancellationToken, Task> Instant = (_, _) => Task.CompletedTask;

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(5);
        Assert.True(cond(), "condition was not met within the timeout");
    }

    [Fact]
    public async Task Posts_a_retry_for_each_backoff_up_to_the_cap()
    {
        var posts = new ConcurrentQueue<(string Agent, int Attempt)>();
        var scheduler = new ThrottleRetryScheduler(
            postRetry: (id, n) => { posts.Enqueue((id, n)); return Task.CompletedTask; },
            delay: Instant)
        { Backoffs = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero } };

        scheduler.OnThrottled("core-");

        await WaitUntilAsync(() => posts.Count == 3);
        Assert.Equal(new[] { ("core-", 1), ("core-", 2), ("core-", 3) }, posts.ToArray());
    }

    [Fact]
    public async Task Cancels_the_pending_retry_when_the_agent_resumes()
    {
        var posts = new ConcurrentQueue<(string, int)>();
        // Gate the backoff so the test controls exactly when it would elapse. A WORKING cancel ends the await
        // on resume; then releasing the gate is a no-op. A BROKEN cancel would leave the await pending, so
        // the release below would fire a (wrong) retry — that's what this test catches.
        var gate = new TaskCompletionSource();
        Func<TimeSpan, CancellationToken, Task> gated = (_, ct) =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };
        var scheduler = new ThrottleRetryScheduler(
            postRetry: (id, n) => { posts.Enqueue((id, n)); return Task.CompletedTask; },
            delay: gated)
        { Backoffs = new[] { TimeSpan.FromSeconds(20) } };

        scheduler.OnThrottled("core-");
        await Task.Delay(20);          // let it reach the gated backoff
        scheduler.OnResumed("core-");  // agent recovered → cancel the pending retry
        gate.TrySetResult();           // the backoff "elapses" — but a working cancel already ended it

        await Task.Delay(50);
        Assert.Empty(posts);           // resume cancelled the retry before it could fire
    }

    [Fact]
    public async Task Stops_after_the_cap_and_does_not_retry_forever()
    {
        var posts = new ConcurrentQueue<(string, int)>();
        var scheduler = new ThrottleRetryScheduler(
            postRetry: (id, n) => { posts.Enqueue((id, n)); return Task.CompletedTask; },
            delay: Instant)
        { Backoffs = new[] { TimeSpan.Zero, TimeSpan.Zero } };   // cap = 2

        scheduler.OnThrottled("core-");

        await WaitUntilAsync(() => posts.Count == 2);
        await Task.Delay(30);                 // give any (wrongly) scheduled extra retry time to fire
        Assert.Equal(2, posts.Count);         // bounded — no third attempt
    }

    [Fact]
    public async Task Does_not_start_a_second_sequence_while_one_is_running()
    {
        var posts = new ConcurrentQueue<(string, int)>();
        // A gate delay keeps the first sequence PARKED (still running) so the duplicate signal is observably
        // rejected — an instant delay would let the first sequence finish before the duplicate arrives.
        var gate = new TaskCompletionSource();
        Func<TimeSpan, CancellationToken, Task> gated = (_, ct) =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };
        var scheduler = new ThrottleRetryScheduler(
            postRetry: (id, n) => { posts.Enqueue((id, n)); return Task.CompletedTask; },
            delay: gated)
        { Backoffs = new[] { TimeSpan.Zero } };   // one attempt per sequence

        scheduler.OnThrottled("core-");
        scheduler.OnThrottled("core-");   // duplicate — first is still parked, must NOT start a parallel sequence

        gate.SetResult();   // release the single parked backoff
        await WaitUntilAsync(() => !posts.IsEmpty);
        await Task.Delay(30);
        Assert.Single(posts);   // one sequence ran → exactly one retry, not two
    }
}
