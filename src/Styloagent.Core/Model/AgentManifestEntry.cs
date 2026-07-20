namespace Styloagent.Core.Model;

public enum AgentRuntimeKind { Claude, Codex }

public sealed record AgentManifestEntry(
    string Prefix,
    string Repo,
    string Worktree,
    string LaunchPromptPath,
    string RestartPromptPath,
    string SavedContextPath,
    AgentTransport Transport,
    AgentRuntimeKind Runtime = AgentRuntimeKind.Claude,
    string? Model = null,
    string? Effort = null);
