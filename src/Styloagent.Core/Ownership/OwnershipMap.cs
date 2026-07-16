using System.Text;
using System.Text.RegularExpressions;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Ownership;

/// <summary>
/// File-level ownership resolver: maps a repo-relative path to the agent prefix that owns it, with the
/// MOST-SPECIFIC matching glob winning (so a <c>session-</c> carve-out inside <c>cockpit-</c>'s
/// <c>src/Styloagent.App/**</c> wins over the broad glob). This is the machine-readable projection of
/// <c>architecture.md</c> (<c>.styloagent/ownership.yaml</c>) — Slice 1/2 of the ownership-enforcement
/// design (docs/superpowers/specs/2026-07-15-ownership-enforcement-design.md). Pure and never-throws.
///
/// Scope: this resolves OWNERSHIP only. Gate policy — <c>overview-</c> bypass, unowned ⇒ allow,
/// obj/bin/tests/docs exemptions, leases — belongs to the PreToolUse gate that consumes
/// <see cref="OwnerOf"/>, not here. Keeping the resolver pure keeps it trivially testable.
/// </summary>
public sealed class OwnershipMap
{
    private readonly OwnerGlob[] _globs;

    private OwnershipMap(OwnerGlob[] globs) => _globs = globs;

    /// <summary>A map that owns nothing (every path resolves to <c>null</c>/unowned).</summary>
    public static OwnershipMap Empty { get; } = new(Array.Empty<OwnerGlob>());

    /// <summary>
    /// The owner prefix for <paramref name="path"/> (a repo-relative path; back-slashes and a leading
    /// <c>./</c> are normalised), or <c>null</c> when no glob matches (unowned ⇒ shared).
    /// </summary>
    public string? OwnerOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string norm = Normalize(path);
        OwnerGlob? best = null;
        foreach (var g in _globs)
        {
            if (!g.Regex.IsMatch(norm)) continue;
            // Most-specific wins; ties break deterministically on the pattern text so results are stable.
            if (best is null
                || g.Specificity > best.Specificity
                || (g.Specificity == best.Specificity && string.CompareOrdinal(g.Pattern, best.Pattern) > 0))
                best = g;
        }
        return best?.Owner;
    }

    /// <summary>Build from an in-memory owner ⇒ globs map (the pure entry point used by tests).</summary>
    public static OwnershipMap From(IReadOnlyDictionary<string, IReadOnlyList<string>> owners)
    {
        var globs = new List<OwnerGlob>();
        foreach (var (owner, patterns) in owners)
        {
            if (string.IsNullOrWhiteSpace(owner) || patterns is null) continue;
            foreach (var p in patterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string pat = Normalize(p);
                globs.Add(new OwnerGlob(owner.Trim(), pat, Compile(pat), Specificity(pat)));
            }
        }
        return new OwnershipMap(globs.ToArray());
    }

    /// <summary>Load from an <c>ownership.yaml</c> manifest. Missing/unreadable ⇒ <see cref="Empty"/>.</summary>
    public static OwnershipMap Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : Empty; }
        catch { return Empty; }
    }

    /// <summary>Parse an <c>ownership.yaml</c> byte payload. Invalid YAML ⇒ <see cref="Empty"/> (never throws).</summary>
    public static OwnershipMap Parse(ReadOnlyMemory<byte> yaml)
    {
        try
        {
            var m = YamlSerializer.Deserialize<OwnershipManifest>(yaml);
            if (m?.Owners is null || m.Owners.Count == 0) return Empty;
            var dict = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var (k, v) in m.Owners) dict[k] = v ?? new List<string>();
            return From(dict);
        }
        catch { return Empty; }
    }

    private static string Normalize(string p)
    {
        string s = p.Replace('\\', '/').Trim();
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s.Substring(2);
        return s.TrimStart('/');
    }

    // ** ⇒ any chars incl '/'; * ⇒ any chars except '/'; everything else literal.
    private static Regex Compile(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                else sb.Append("[^/]*");
            }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }

    // Specificity = count of literal (non-wildcard) characters, so an exact path beats a '**' glob.
    private static int Specificity(string glob) => glob.Count(c => c != '*');

    private sealed record OwnerGlob(string Owner, string Pattern, Regex Regex, int Specificity);
}

[YamlObject]
internal partial class OwnershipManifest
{
    public Dictionary<string, List<string>> Owners { get; set; } = new();
}
