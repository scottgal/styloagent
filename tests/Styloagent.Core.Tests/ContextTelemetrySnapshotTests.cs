using Styloagent.Core.Model;
using Styloagent.Core.Sessions;
using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Tests;

public sealed class ContextTelemetrySnapshotTests
{
    [Theory]
    [InlineData(AgentRuntimeKind.Codex, ContextTelemetrySource.CodexTranscript)]
    [InlineData(AgentRuntimeKind.Claude, ContextTelemetrySource.ClaudeTranscript)]
    [InlineData(AgentRuntimeKind.ClaudeDeepSeek, ContextTelemetrySource.ClaudeTranscript)]
    public void Observe_normalizes_every_supported_runtime(AgentRuntimeKind runtime, ContextTelemetrySource source)
    {
        var key = ContextSnapshotKey.Create("/repo-a", "worker-", "session-1");
        var snapshot = new ContextTelemetryStore().Observe(key, runtime, new TranscriptUsage(20, 100, "actual-model"), "stale-default", "medium");

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(source, snapshot.Source);
        Assert.Equal("actual-model", snapshot.Model);
        Assert.Equal(80, snapshot.RemainingTokens);
        Assert.Equal(0.8, snapshot.RemainingFraction);
        Assert.Equal(ContextPressure.Normal, snapshot.Pressure);
    }

    [Fact]
    public void Unknown_telemetry_is_explicitly_unavailable_not_zero_or_critical()
    {
        var key = ContextSnapshotKey.Create("/repo-a", "worker-", "session-1");
        var snapshot = new ContextTelemetryStore().Observe(key, AgentRuntimeKind.Codex, null);

        Assert.False(snapshot.IsAvailable);
        Assert.Null(snapshot.RemainingTokens);
        Assert.Null(snapshot.RemainingFraction);
        Assert.Equal(ContextPressure.Unknown, snapshot.Pressure);
    }

    [Fact]
    public void Partial_or_non_telemetry_frames_retain_last_trustworthy_observation()
    {
        var key = ContextSnapshotKey.Create("/repo-a", "worker-", "session-1");
        var store = new ContextTelemetryStore();
        var observedAt = DateTimeOffset.Parse("2026-08-18T12:00:00Z");
        var checkedAt = observedAt.AddMinutes(5);
        var first = store.Observe(key, AgentRuntimeKind.Codex, new TranscriptUsage(40, 100, "gpt-actual"), observedAt: observedAt);
        var afterApprovalOrResume = store.Observe(key, AgentRuntimeKind.Codex, null, observedAt: checkedAt);

        Assert.Equal(first.RemainingTokens, afterApprovalOrResume.RemainingTokens);
        Assert.Equal("gpt-actual", afterApprovalOrResume.Model);
        Assert.Equal(ContextTelemetryConfidence.Retained, afterApprovalOrResume.Confidence);
        Assert.Equal(observedAt, afterApprovalOrResume.ObservedAt);
        Assert.Equal(checkedAt, afterApprovalOrResume.CheckedAt);
    }

    [Fact]
    public void Repo_scoping_prevents_duplicate_prefixes_from_leaking_context()
    {
        var store = new ContextTelemetryStore();
        var repoA = ContextSnapshotKey.Create("/repo-a", "session-", "same-session");
        var repoB = ContextSnapshotKey.Create("/repo-b", "session-", "same-session");
        store.Observe(repoA, AgentRuntimeKind.Claude, new TranscriptUsage(10, 100, null));

        Assert.True(store.Get(repoA, AgentRuntimeKind.Claude).IsAvailable);
        Assert.False(store.Get(repoB, AgentRuntimeKind.Claude).IsAvailable);
    }

    [Fact]
    public void Zero_used_tokens_is_a_valid_normal_observation()
    {
        var key = ContextSnapshotKey.Create("/repo-a", "worker-", "session-1");
        var snapshot = new ContextTelemetryStore().Observe(key, AgentRuntimeKind.Codex, new TranscriptUsage(0, 128_000, "gpt"));

        Assert.True(snapshot.IsAvailable);
        Assert.Equal(128_000, snapshot.RemainingTokens);
        Assert.Equal(1, snapshot.RemainingFraction);
        Assert.Equal(ContextPressure.Normal, snapshot.Pressure);
    }

    [Fact]
    public void Mismatched_runtime_never_returns_a_prior_snapshot_for_the_same_identity()
    {
        var key = ContextSnapshotKey.Create("/repo-a", "worker-", "session-1");
        var store = new ContextTelemetryStore();
        store.Observe(key, AgentRuntimeKind.Codex, new TranscriptUsage(40, 100, "gpt"));

        var snapshot = store.Get(key, AgentRuntimeKind.Claude);
        var retained = store.Observe(key, AgentRuntimeKind.Claude, null);

        Assert.False(snapshot.IsAvailable);
        Assert.False(retained.IsAvailable);
        Assert.Null(retained.RemainingTokens);
    }
}
