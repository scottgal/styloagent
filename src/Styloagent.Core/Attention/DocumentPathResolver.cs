namespace Styloagent.Core.Attention;

/// <summary>The outcome of resolving an <c>open_document</c> path: an OK canonical path, or a rejection reason.</summary>
public sealed record DocumentOpenResolution(bool Ok, string? Path, string? Error)
{
    public static DocumentOpenResolution Resolved(string path) => new(true, path, null);
    public static DocumentOpenResolution Rejected(string error) => new(false, null, error);
}

/// <summary>
/// Resolves + validates an <c>open_document</c> path: canonicalizes it (collapsing <c>..</c> via
/// <see cref="System.IO.Path.GetFullPath(string, string)"/>, so a traversal can't escape scope), scopes the
/// result to within an open repo root, and confirms the file exists. A repo-relative path resolves against the
/// SENDER's own repo root (the doc the agent naturally holds); an absolute path passes straight through (still
/// collapsed). Never throws — a bad path degrades to a rejection reason, matching the graceful-degrade posture
/// of the whole operator-attention surface.
/// </summary>
public static class DocumentPathResolver
{
    public static DocumentOpenResolution Resolve(
        string? rawPath, string? senderRepoRoot, IReadOnlyCollection<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return DocumentOpenResolution.Rejected("path is required");

        var roots = allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(NormalizeRoot)
            .ToList();
        if (roots.Count == 0)
            return DocumentOpenResolution.Rejected("no open repo to scope the document to");

        // Relative paths resolve against the sender's repo root (the doc the agent holds); fall back to the
        // first open root when the sender's root is unknown. GetFullPath collapses any '.'/'..' either way.
        var baseDir = string.IsNullOrWhiteSpace(senderRepoRoot) ? roots[0] : NormalizeRoot(senderRepoRoot);

        string full;
        try { full = Path.GetFullPath(rawPath.Trim(), baseDir); }
        catch { return DocumentOpenResolution.Rejected($"'{rawPath}' is not a valid path"); }

        if (!roots.Any(r => IsWithin(full, r)))
            return DocumentOpenResolution.Rejected($"'{full}' is outside every open repo");

        if (!File.Exists(full))
            return DocumentOpenResolution.Rejected($"'{full}' does not exist");

        return DocumentOpenResolution.Resolved(full);
    }

    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));

    /// <summary>True when <paramref name="fullPath"/> is the root itself or lives beneath it (case-insensitive:
    /// robust on case-preserving macOS/Windows filesystems; both operands come from trusted <c>GetFullPath</c>).</summary>
    private static bool IsWithin(string fullPath, string root) =>
        fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
