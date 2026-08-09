using System.Text;

namespace Styloagent.BrowserBroker;

/// <summary>
/// Portable artifact references so ONE client can hand a screenshot to ANOTHER client. A completed
/// run's artifact is identified by <c>broker://&lt;runId&gt;/&lt;fileName&gt;</c> — a value that carries no
/// absolute path, so it is safe to put in a bus message, a mission doc, or a chat reply. The receiving
/// client resolves it against ITS OWN browser root (or the same shared artifacts root when clients share
/// one), so a screenshot taken by one cockpit/agent can be opened by another.
/// </summary>
public static class BrokerArtifacts
{
    /// <summary>The URI scheme for portable broker artifact references.</summary>
    public const string Scheme = "broker";

    /// <summary>Returns the reference for a completed run's artifact file, or null when not available.</summary>
    public static string? Reference(string? artifactPath, string browserRoot)
    {
        if (string.IsNullOrWhiteSpace(artifactPath)) return null;
        var full = Path.GetFullPath(artifactPath);
        var root = Path.GetFullPath(browserRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var relative = Path.GetRelativePath(root, full);
        // relative = artifacts/<runId>/<fileName>
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 3 || parts[0] != "artifacts") return null;
        return $"{Scheme}://{Uri.EscapeDataString(parts[1])}/{Uri.EscapeDataString(parts[2])}";
    }

    /// <summary>
    /// Resolves a reference (or raw filesystem path) to a local file path under
    /// <paramref name="browserRoot"/>. Returns null when the reference is unknown or the file is missing.
    /// </summary>
    public static string? Resolve(string? referenceOrPath, string browserRoot)
    {
        if (string.IsNullOrWhiteSpace(referenceOrPath)) return null;
        var value = referenceOrPath.Trim();

        if (value.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[(Scheme.Length + 3)..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return null;
            var runId = Uri.UnescapeDataString(rest[..slash]);
            var fileName = Uri.UnescapeDataString(rest[(slash + 1)..]);
            if (fileName.Contains('/') || fileName.Contains('\\')) return null;   // never escape the artifacts dir
            var path = Path.Combine(browserRoot, "artifacts", runId, fileName);
            return File.Exists(path) ? path : null;
        }

        // Raw path (local usage / tests): accept only inside the browser root, else null.
        var full = Path.GetFullPath(value);
        var root = Path.GetFullPath(browserRoot);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full)
            ? full
            : null;
    }

    /// <summary>True when the value looks like a broker reference.</summary>
    public static bool IsReference(string? value)
        => !string.IsNullOrWhiteSpace(value) && value!.TrimStart().StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase);
}
