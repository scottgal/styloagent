namespace Styloagent.Core.Git;

/// <summary>The role of a line in a unified diff.</summary>
public enum DiffLineKind { Header, Added, Deleted, Context }

/// <summary>One line of a unified diff, with its old/new line numbers (0 when N/A).</summary>
public sealed record DiffLine(DiffLineKind Kind, string Content, int OldLine, int NewLine);

/// <summary>A parsed unified diff for a single file.</summary>
public sealed record FileDiff(string Path, int Added, int Deleted, bool IsBinary, IReadOnlyList<DiffLine> Lines)
{
    public static FileDiff Empty(string path) => new(path, 0, 0, false, System.Array.Empty<DiffLine>());
}
