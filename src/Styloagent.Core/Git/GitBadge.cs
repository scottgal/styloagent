using System.Text;

namespace Styloagent.Core.Git;

/// <summary>Formats a compact one-line git badge for the roster (pure, UI-free).</summary>
public static class GitBadge
{
    public static string Format(GitStatus? status, bool hasWorktree)
    {
        if (!hasWorktree || status is null) return "";
        if (status.HasConflicts) return "⚠ conflict";
        if (!status.IsDirty && status.Ahead == 0 && status.Behind == 0) return "✓";

        var sb = new StringBuilder();
        if (status.Ahead > 0) sb.Append($"↑{status.Ahead} ");
        if (status.Behind > 0) sb.Append($"↓{status.Behind} ");
        if (status.IsDirty) sb.Append('✎');
        return sb.ToString().TrimEnd();
    }
}
