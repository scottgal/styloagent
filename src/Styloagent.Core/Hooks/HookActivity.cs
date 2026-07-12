namespace Styloagent.Core.Hooks;

/// <summary>
/// Maps a Claude Code tool name (from a <c>PreToolUse</c>/<c>PostToolUse</c> hook) to a short,
/// human-scannable activity phrase for the roster — the "what is it doing right now" line
/// (e.g. <c>Read</c> → "reading files", <c>Bash</c> → "running commands"). Pure and total:
/// an unknown tool falls back to its own lowercased name so nothing ever reads as blank.
/// </summary>
public static class HookActivity
{
    public static string DescribeTool(string? toolName) => toolName switch
    {
        null or ""                                  => "",
        "Read" or "NotebookRead"                    => "reading files",
        "Grep" or "Glob" or "LS"                    => "searching code",
        "Edit" or "MultiEdit" or "Write"
            or "NotebookEdit"                       => "editing",
        "Bash" or "BashOutput" or "KillShell"       => "running commands",
        "Task" or "Agent"                           => "delegating",
        "WebFetch" or "WebSearch"                   => "searching web",
        "TodoWrite"                                 => "planning",
        _ when toolName.StartsWith("mcp__",
            System.StringComparison.Ordinal)        => "using a tool",
        _                                           => toolName.ToLowerInvariant(),
    };
}
