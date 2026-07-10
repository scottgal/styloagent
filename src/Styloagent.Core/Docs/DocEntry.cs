namespace Styloagent.Core.Docs;

/// <summary>Where a document came from — groups the library by source.</summary>
public enum DocSource
{
    Repo,
    Channel,
}

/// <summary>
/// A single markdown document in the library: its display title, absolute path, source, and the
/// path relative to its source root (used for grouping/labels).
/// </summary>
public sealed record DocEntry(string Title, string FullPath, DocSource Source, string RelativePath);
