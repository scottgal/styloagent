using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.App.Config;

/// <summary>
/// Cockpit-only presentation record — border colour and display name per agent prefix.
/// NOT the manifest; this sidecar lives in the App's own config directory.
/// </summary>
public sealed record AgentPresentation(string Prefix, string DisplayName, string BorderColorHex);

[YamlObject]
internal partial class PresentationFile
{
    public List<PresentationRow> Agents { get; set; } = new();
}

[YamlObject]
internal partial class PresentationRow
{
    public string Prefix { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string BorderColorHex { get; set; } = "";
}

/// <summary>
/// Loads and saves the cockpit-only presentation sidecar (VYaml, same pattern as ManifestStore).
/// </summary>
public sealed class PresentationStore
{
    // Fixed palette used by DefaultColorFor — 16 distinct, accessible colours.
    private static readonly string[] Palette =
    [
        "#E57373", "#F06292", "#BA68C8", "#7986CB",
        "#4FC3F7", "#4DB6AC", "#81C784", "#DCE775",
        "#FFB74D", "#FF8A65", "#A1887F", "#90A4AE",
        "#F48FB1", "#80CBC4", "#AED581", "#FFF176",
    ];

    /// <summary>
    /// Deterministically derives a stable border colour from the agent prefix by hashing a
    /// NORMALIZED form of the prefix and picking from the fixed palette. Normalizing (see
    /// <see cref="NormalizeColorKey"/>) is what aligns an agent's terminal/roster colour with its
    /// bus messages: a worktree agent (<c>"wt-foss-"</c> / <c>"foss-"</c>) and its channel routing
    /// prefix (<c>"foss-"</c>) collapse to the same key → the same colour. Same logical agent →
    /// same colour, everywhere, every run.
    /// </summary>
    public static string DefaultColorFor(string prefix)
    {
        string key = NormalizeColorKey(prefix);

        // Simple but stable FNV-1a 32-bit hash over the normalized key.
        uint hash = 2166136261u;
        foreach (var c in key)
        {
            hash ^= (byte)c;
            hash *= 16777619u;
        }
        return Palette[hash % (uint)Palette.Length];
    }

    /// <summary>
    /// Normalizes an agent/routing prefix to a shared colour key so equivalent identities colour
    /// alike: lower-cased, trailing separators dropped, and a leading worktree marker removed.
    /// Examples: <c>"foss-"</c>, <c>"FOSS"</c>, <c>"wt-foss"</c> → <c>"foss"</c>.
    /// </summary>
    public static string NormalizeColorKey(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;

        string key = prefix.Trim().ToLowerInvariant().Trim('-', '_', '/', ' ');
        foreach (string marker in new[] { "wt-", "worktree-", "wt/", "worktrees/" })
        {
            if (key.StartsWith(marker, StringComparison.Ordinal))
            {
                key = key[marker.Length..].Trim('-', '_', '/', ' ');
                break;
            }
        }
        return key;
    }

    public async Task SaveAsync(string path, IReadOnlyList<AgentPresentation> presentations)
    {
        var file = new PresentationFile
        {
            Agents = presentations.Select(p => new PresentationRow
            {
                Prefix = p.Prefix,
                DisplayName = p.DisplayName,
                BorderColorHex = p.BorderColorHex,
            }).ToList(),
        };
        var bytes = YamlSerializer.Serialize(file);
        await File.WriteAllBytesAsync(path, bytes.ToArray());
    }

    public async Task<IReadOnlyList<AgentPresentation>> LoadAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var file = YamlSerializer.Deserialize<PresentationFile>(new ReadOnlyMemory<byte>(bytes));
        return file.Agents.Select(r => new AgentPresentation(
            r.Prefix,
            r.DisplayName,
            r.BorderColorHex)).ToList();
    }
}
