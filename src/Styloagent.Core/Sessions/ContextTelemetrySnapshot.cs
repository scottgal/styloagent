using System.Collections.Concurrent;
using Styloagent.Core.Model;
using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Sessions;

/// <summary>Where a context observation came from. A missing observation is never represented as zero usage.</summary>
public enum ContextTelemetrySource { Unavailable, CodexTranscript, ClaudeTranscript }

/// <summary>How reliable the current observation is. Retained means a non-telemetry frame followed a valid reading.</summary>
public enum ContextTelemetryConfidence { Unavailable, Observed, Retained }

/// <summary>
/// Stable, repo-scoped identity for context telemetry. Prefixes are only unique within a repo; a session id
/// distinguishes a resumed agent from a new process that reuses the same prefix.
/// </summary>
public readonly record struct ContextSnapshotKey(string Repo, string AgentId, string SessionId)
{
    public static ContextSnapshotKey Create(string? repo, string? agentId, string? sessionId)
    {
        var normalizedRepo = string.IsNullOrWhiteSpace(repo) ? "" : Path.TrimEndingDirectorySeparator(repo.Trim());
        var normalizedAgent = agentId?.Trim() ?? "";
        var normalizedSession = sessionId?.Trim() ?? "";
        if (normalizedAgent.Length == 0) throw new ArgumentException("An agent identity is required.", nameof(agentId));
        return new ContextSnapshotKey(normalizedRepo, normalizedAgent, normalizedSession);
    }
}

/// <summary>
/// Immutable normalized context observation. Token and fraction fields are nullable when telemetry is unavailable;
/// consumers must therefore render unavailable explicitly instead of implying an exhausted context window.
/// </summary>
public sealed record ContextTelemetrySnapshot(
    ContextSnapshotKey Key,
    AgentRuntimeKind Runtime,
    string? Model,
    string? Effort,
    long? LimitTokens,
    long? UsedTokens,
    long? RemainingTokens,
    double? RemainingFraction,
    ContextPressure Pressure,
    DateTimeOffset? ObservedAt,
    DateTimeOffset CheckedAt,
    ContextTelemetrySource Source,
    ContextTelemetryConfidence Confidence,
    string? ConfiguredModel = null,
    string? ConfiguredEffort = null)
{
    public bool IsAvailable => Confidence != ContextTelemetryConfidence.Unavailable;

    public static ContextTelemetrySnapshot Unavailable(ContextSnapshotKey key, AgentRuntimeKind runtime, string? model = null, string? effort = null)
        => new(key, runtime, null, null, null, null, null, null, ContextPressure.Unknown,
            null, DateTimeOffset.UtcNow, ContextTelemetrySource.Unavailable, ContextTelemetryConfidence.Unavailable,
            model, effort);

    public static bool TryCreate(ContextSnapshotKey key, AgentRuntimeKind runtime, TranscriptUsage? usage,
        string? configuredModel, string? effort, ContextTelemetrySource source, DateTimeOffset observedAt,
        out ContextTelemetrySnapshot snapshot)
    {
        snapshot = null!;
        if (usage is null || usage.WindowTokens <= 0 || usage.ContextTokens < 0 || usage.ContextTokens > usage.WindowTokens)
            return false;

        var used = usage.ContextTokens;
        var limit = usage.WindowTokens;
        var remaining = limit - used;
        var usedFraction = (double)used / limit;
        snapshot = new ContextTelemetrySnapshot(key, runtime, usage.Model, null,
            limit, used, remaining, (double)remaining / limit, ContextPressurePolicy.For(usedFraction),
            observedAt, observedAt, source, ContextTelemetryConfidence.Observed, configuredModel, effort);
        return true;
    }

    /// <summary>Records a successful check without misrepresenting the age of the last actual observation.</summary>
    public ContextTelemetrySnapshot Retain(DateTimeOffset checkedAt, string? configuredModel, string? configuredEffort)
        => this with
        {
            CheckedAt = checkedAt,
            Confidence = ContextTelemetryConfidence.Retained,
            ConfiguredModel = configuredModel,
            ConfiguredEffort = configuredEffort
        };
}

/// <summary>
/// Canonical, process-local source for normalized context telemetry. Invalid, partial, ANSI/terminal, approval,
/// tool-idle, compaction, and resume frames never overwrite a prior trustworthy transcript observation.
/// </summary>
public sealed class ContextTelemetryStore
{
    private readonly ConcurrentDictionary<ContextSnapshotKey, ContextTelemetrySnapshot> _snapshots = new();

    public ContextTelemetrySnapshot Observe(ContextSnapshotKey key, AgentRuntimeKind runtime, TranscriptUsage? usage,
        string? configuredModel = null, string? effort = null, DateTimeOffset? observedAt = null)
    {
        var source = runtime == AgentRuntimeKind.Codex
            ? ContextTelemetrySource.CodexTranscript : ContextTelemetrySource.ClaudeTranscript;
        var now = observedAt ?? DateTimeOffset.UtcNow;
        if (ContextTelemetrySnapshot.TryCreate(key, runtime, usage, configuredModel, effort, source, now, out var observed))
        {
            _snapshots[key] = observed;
            return observed;
        }

        if (_snapshots.TryGetValue(key, out var previous) && previous.Runtime == runtime)
        {
            var retained = previous.Retain(now, configuredModel, effort);
            _snapshots[key] = retained;
            return retained;
        }

        return ContextTelemetrySnapshot.Unavailable(key, runtime, configuredModel, effort);
    }

    public ContextTelemetrySnapshot Get(ContextSnapshotKey key, AgentRuntimeKind runtime, string? model = null, string? effort = null)
        => _snapshots.TryGetValue(key, out var snapshot) && snapshot.Runtime == runtime
            ? snapshot
            : ContextTelemetrySnapshot.Unavailable(key, runtime, model, effort);
}
