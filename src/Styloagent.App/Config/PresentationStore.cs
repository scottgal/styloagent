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
    /// Deterministically derives a stable border colour from the agent prefix by hashing
    /// the prefix bytes and picking from the fixed palette.  Same prefix → same colour,
    /// every run.
    /// </summary>
    public static string DefaultColorFor(string prefix)
    {
        // Simple but stable FNV-1a 32-bit hash over the UTF-8 bytes.
        uint hash = 2166136261u;
        foreach (var c in prefix)
        {
            hash ^= (byte)c;
            hash *= 16777619u;
        }
        return Palette[hash % (uint)Palette.Length];
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
