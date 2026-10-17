using Styloagent.Core.Channel;
using Styloagent.Core.Hooks;
using Styloagent.Core.Transcripts;

namespace Styloagent.Core.Sessions;

/// <summary>Structured agent control independent of the terminal used to display it.</summary>
public interface INativeAgentSession
{
    string SessionId { get; }
    string? Model { get; }
    string? Effort { get; }
    TranscriptUsage? Usage { get; }
    string? PendingPermissionId { get; }
    event Action<HookEvent>? RuntimeEvent;
    Task SendAsync(string text, string messageId, CancellationToken ct = default);
    Task DeliverAsync(BusMessage message, DeliveryMode mode, CancellationToken ct = default);
    Task ReplyToPermissionAsync(string decision, CancellationToken ct = default);
    Task InterruptAsync(bool resume, CancellationToken ct = default);
}

public sealed record OpenCodeOptions(
    string Prefix, string RepoRoot, string HookId, string? HooksDirectory = null,
    Uri? McpUrl = null, string? Instructions = null,
    FleetPermissionMode PermissionMode = FleetPermissionMode.Scoped,
    IReadOnlyList<string>? GateCommand = null, string? StateDirectory = null);
