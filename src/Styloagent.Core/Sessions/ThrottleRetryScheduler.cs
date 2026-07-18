using System.Collections.Concurrent;

namespace Styloagent.Core.Sessions;

/// <summary>
/// Auto-recovery for throttled agents. When an agent enters a throttle episode (<see cref="OnThrottled"/>)
/// and does NOT resume on its own, this posts a retry nudge with EXPONENTIAL BACKOFF (default 20s → 60s →
/// 180s), bounded to the number of <see cref="Backoffs"/> so a permanently-stuck agent can't retry forever.
/// The sequence is CANCELLED the moment the agent resumes (<see cref="OnResumed"/>).
/// </summary>
/// <remarks>
/// The retry itself is posted via the injected <c>postRetry</c> delegate — the caller wires it to the
/// existing bus send (ChannelMessageWriter.Write + delivery pump) so the retry is a visible, traceable bus
/// message that rides the normal delivery path (and therefore the PtyMessageInjector compose-defer, so a
/// retry never types over the operator). The backoff <c>delay</c> is injected so it's deterministic in tests.
/// No new Core/Channel delayed-delivery primitive is needed — the backoff is just a timer here.
/// </remarks>
public sealed class ThrottleRetryScheduler
{
    private readonly Func<string, int, Task> _postRetry;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <param name="postRetry">Posts a retry for (agentId, attemptNumber). Wire to the bus send + pump.</param>
    /// <param name="delay">Backoff delay (defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>); injected for tests.</param>
    public ThrottleRetryScheduler(Func<string, int, Task> postRetry, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _postRetry = postRetry;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Backoff schedule; its length is the attempt cap. Default ~20s / 60s / 180s.</summary>
    public IReadOnlyList<TimeSpan> Backoffs { get; init; } = new[]
    {
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(180),
    };

    /// <summary>The agent entered a throttle episode — start the backoff-retry sequence (idempotent per agent).</summary>
    public void OnThrottled(string agentId)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(agentId, cts)) { cts.Dispose(); return; }   // a sequence is already running
        _ = RunAsync(agentId, cts);
    }

    /// <summary>The agent resumed — cancel any pending retries for it.</summary>
    public void OnResumed(string agentId)
    {
        if (_running.TryGetValue(agentId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* raced with RunAsync's cleanup — already done */ }
        }
    }

    private async Task RunAsync(string agentId, CancellationTokenSource cts)
    {
        try
        {
            for (int attempt = 0; attempt < Backoffs.Count; attempt++)
            {
                await _delay(Backoffs[attempt], cts.Token).ConfigureAwait(false);
                await _postRetry(agentId, attempt + 1).ConfigureAwait(false);
            }
            // Cap reached — leave the agent throttled + flagged for the operator; stop retrying.
        }
        catch (OperationCanceledException) { /* agent resumed — stop */ }
        catch { /* a failed post must never crash the app; drop this sequence */ }
        finally
        {
            _running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(agentId, cts));
            cts.Dispose();
        }
    }
}
