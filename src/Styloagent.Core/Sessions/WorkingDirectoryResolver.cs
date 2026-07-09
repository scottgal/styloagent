namespace Styloagent.Core.Sessions;

/// <summary>
/// Resolves a valid, existing working directory to launch an agent process in.
/// Porta.Pty throws <see cref="System.ArgumentNullException"/> on an empty <c>Cwd</c>,
/// so a spawn must never be handed an empty or non-existent directory. This picks the
/// first usable directory: the preferred one (an agent's worktree), then a fallback
/// (an app-level default), then the current process directory as a last resort.
/// </summary>
public static class WorkingDirectoryResolver
{
    public static string Resolve(string? preferred, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && Directory.Exists(preferred))
            return preferred!;
        if (!string.IsNullOrWhiteSpace(fallback) && Directory.Exists(fallback))
            return fallback!;
        return Directory.GetCurrentDirectory();
    }
}
