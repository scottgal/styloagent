using System.Text;

namespace Styloagent.Core.Diagrams;

public sealed record FleetNode(string Prefix, string? ParentPrefix, string Responsibility, string State);

/// <summary>Renders the agent fleet tree as a mermaid flowchart (graph TD). Pure, total, deterministic.</summary>
public static class SystemMapGenerator
{
    public static string Build(IEnumerable<FleetNode> nodes)
    {
        var list = nodes.OrderBy(n => n.Prefix, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.Append("# System Map\n\n```mermaid\ngraph TD\n");
        if (list.Count == 0)
        {
            sb.Append("    empty[\"no agents yet\"]\n```\n");
            return sb.ToString();
        }
        foreach (var n in list)
            sb.Append($"    {Id(n.Prefix)}[\"{Escape(n.Prefix)}<br/>{Escape(n.Responsibility)}\"]\n");
        foreach (var n in list.Where(n => !string.IsNullOrWhiteSpace(n.ParentPrefix)))
            sb.Append($"    {Id(n.ParentPrefix!)} --> {Id(n.Prefix)}\n");
        sb.Append("    classDef working fill:#12351f,stroke:#3fb950,color:#e6edf3;\n");
        sb.Append("    classDef idle fill:#21262d,stroke:#8b949e,color:#e6edf3;\n");
        sb.Append("    classDef needsYou fill:#3a2a00,stroke:#e5a05a,color:#e6edf3;\n");
        sb.Append("    classDef exited fill:#3d1417,stroke:#f85149,color:#e6edf3;\n");
        foreach (var n in list)
        {
            var cls = StateClass(n.State);
            if (cls is not null) sb.Append($"    class {Id(n.Prefix)} {cls};\n");
        }
        sb.Append("```\n");
        return sb.ToString();
    }

    /// <summary>Sanitizes a prefix into a valid mermaid/C4 node id (used as the element id, so hosts
    /// can map a clicked component back to its agent).</summary>
    public static string Id(string prefix)
    {
        var chars = prefix.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray();
        var id = new string(chars);
        if (id.Length == 0) id = "n";
        if (char.IsAsciiDigit(id[0])) id = "n" + id;
        return id;
    }

    internal static string Escape(string s) => s.Replace("\"", "'", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string? StateClass(string state) => state switch
    {
        "working" => "working",
        "idle" => "idle",
        "needs you" => "needsYou",
        "exited" => "exited",
        _ => null,
    };
}
