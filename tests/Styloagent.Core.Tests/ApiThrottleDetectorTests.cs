using System;
using System.Collections.Generic;
using Styloagent.Core.Sessions;
using Xunit;

namespace Styloagent.Core.Tests;

/// <summary>
/// <see cref="ApiThrottleDetector"/> taps an agent's PTY output and flags API-error / rate-limit episodes
/// (which fire NO Claude Code hook, so the cockpit would otherwise miss them). It debounces to one signal
/// per episode and clears when the agent makes forward progress again.
/// </summary>
public class ApiThrottleDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    private const string RateLimit = "⏺ API Error: Server is temporarily limiting requests · Rate limited";

    [Fact]
    public void Detects_an_api_error_and_raises_throttled_once()
    {
        var det = new ApiThrottleDetector("core-");
        var events = new List<ThrottleEvent>();
        det.Changed += events.Add;

        det.Feed(RateLimit, T0);

        Assert.True(det.IsThrottled);
        var ev = Assert.Single(events);
        Assert.Equal("core-", ev.AgentId);
        Assert.True(ev.IsThrottled);
        Assert.False(string.IsNullOrEmpty(ev.Signature));
        Assert.Equal(T0, ev.Timestamp);
    }

    [Fact]
    public void A_redrawn_error_banner_does_not_refire_within_the_same_episode()
    {
        var det = new ApiThrottleDetector("core-");
        var events = new List<ThrottleEvent>();
        det.Changed += events.Add;

        det.Feed(RateLimit, T0);
        det.Feed(RateLimit, T0.AddMilliseconds(200));   // the TUI redraws the same banner
        det.Feed(RateLimit, T0.AddMilliseconds(400));

        Assert.Single(events);   // ONE episode, one signal
        Assert.True(det.IsThrottled);
    }

    [Theory]
    [InlineData("API Error")]
    [InlineData("rate limited")]
    [InlineData("Server is temporarily limiting requests")]
    [InlineData("Overloaded")]
    [InlineData("overloaded_error")]
    [InlineData("HTTP 429 Too Many Requests")]
    [InlineData("quota exceeded")]
    public void Detects_each_signature_case_insensitively(string chunk)
    {
        var det = new ApiThrottleDetector("a-");
        var events = new List<ThrottleEvent>();
        det.Changed += events.Add;

        det.Feed(chunk, T0);

        Assert.True(det.IsThrottled, $"should detect a throttle signature in: {chunk}");
        Assert.Single(events);
    }

    [Fact]
    public void Clears_throttle_on_forward_progress_after_a_quiet_gap()
    {
        var det = new ApiThrottleDetector("core-") { QuietGap = TimeSpan.FromSeconds(2) };
        var events = new List<ThrottleEvent>();
        det.Changed += events.Add;

        det.Feed(RateLimit, T0);
        // Real, non-error output well after the last error banner → the agent resumed.
        det.Feed("Reading the repository layout to plan the change", T0.AddSeconds(5));

        Assert.False(det.IsThrottled);
        Assert.Equal(2, events.Count);
        Assert.False(events[1].IsThrottled);
        Assert.Equal(T0.AddSeconds(5), events[1].Timestamp);
    }

    [Fact]
    public void Does_not_clear_on_output_within_the_quiet_gap()
    {
        var det = new ApiThrottleDetector("core-") { QuietGap = TimeSpan.FromSeconds(2) };
        var events = new List<ThrottleEvent>();
        det.Changed += events.Add;

        det.Feed(RateLimit, T0);
        // A spinner tick right after the error is NOT forward progress — still throttled.
        det.Feed("⠋ waiting to retry", T0.AddMilliseconds(300));

        Assert.True(det.IsThrottled);
        Assert.Single(events);
    }

    [Fact]
    public void Clears_throttle_on_an_explicit_resume_signal()
    {
        var det = new ApiThrottleDetector("core-");
        var events = new List<ThrottleEvent>();
        det.Changed += events.Add;

        det.Feed(RateLimit, T0);
        det.NoteResumed(T0.AddSeconds(1));   // e.g. the agent's hook state changed to Working

        Assert.False(det.IsThrottled);
        Assert.Equal(2, events.Count);
        Assert.False(events[1].IsThrottled);
    }

    [Fact]
    public void Ordinary_output_when_not_throttled_raises_nothing()
    {
        var det = new ApiThrottleDetector("core-");
        var events = new List<ThrottleEvent>();
        det.Changed += events.Add;

        det.Feed("Running the test suite now", T0);
        det.Feed("All 42 tests passed", T0.AddSeconds(1));

        Assert.False(det.IsThrottled);
        Assert.Empty(events);
    }
}
