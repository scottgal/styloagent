using System.Text;

namespace Styloagent.Core.Diagrams;

/// <summary>A system component in the architecture, owned by an agent (whose colour it wears).</summary>
public sealed record ArchitectureComponent(string Id, string Name, string Responsibility, string? ColorHex);

/// <summary>A directed dependency between two components.</summary>
public sealed record ArchitectureLink(string FromId, string ToId, string? Label);

/// <summary>
/// Renders a system's responsibility decomposition as an ownership-coloured C4 component diagram:
/// each component is filled with its owning agent's identity colour via <c>UpdateElementStyle</c>, so
/// the architecture doubles as a live "who owns what" map (rendered natively + clickably by C4Canvas).
/// Pure, total, deterministic — the styloagent analog of <see cref="SystemMapGenerator"/>.
/// </summary>
public static class C4ResponsibilityGenerator
{
    public static string Build(
        IEnumerable<ArchitectureComponent> components,
        IEnumerable<ArchitectureLink> links,
        string? title = null)
    {
        var comps = components.ToList();
        var sb = new StringBuilder();
        sb.Append("# Architecture\n\n```mermaid\nC4Component\n");
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append($"    title {SystemMapGenerator.Escape(title)}\n");

        if (comps.Count == 0)
        {
            sb.Append("    Component(empty, \"no components yet\", \"\")\n```\n");
            return sb.ToString();
        }

        foreach (var c in comps)
            sb.Append($"    Component({SystemMapGenerator.Id(c.Id)}, \"{SystemMapGenerator.Escape(c.Name)}\", \"{SystemMapGenerator.Escape(c.Responsibility)}\")\n");

        var ids = comps.Select(c => SystemMapGenerator.Id(c.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var l in links)
        {
            var from = SystemMapGenerator.Id(l.FromId);
            var to = SystemMapGenerator.Id(l.ToId);
            if (!ids.Contains(from) || !ids.Contains(to)) continue;   // skip dangling links
            var label = string.IsNullOrWhiteSpace(l.Label) ? "" : $", \"{SystemMapGenerator.Escape(l.Label!)}\"";
            sb.Append($"    Rel({from}, {to}{label})\n");
        }

        // Ownership colour — the component wears its owning agent's identity colour.
        foreach (var c in comps.Where(c => !string.IsNullOrWhiteSpace(c.ColorHex)))
            sb.Append($"    UpdateElementStyle({SystemMapGenerator.Id(c.Id)}, $bgColor=\"{c.ColorHex}\")\n");

        sb.Append("```\n");
        return sb.ToString();
    }
}
