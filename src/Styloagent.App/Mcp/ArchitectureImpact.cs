using System.Text.RegularExpressions;
using MermaidSharp;
using MermaidSharp.Diagrams.C4;

namespace Styloagent.App.Mcp;

/// <summary>
/// Computes the architectural impact of a proposed change to the C4 architecture, as the
/// human-facing "+ Component / − path / Impact:" block — wiring Naiad's C4Diff into the cockpit so a
/// proposal shows its effect before it lands.
/// </summary>
public static partial class ArchitectureImpact
{
    [GeneratedRegex("```mermaid\\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex MermaidBlock();

    /// <summary>Extracts the first C4 mermaid block from markdown (the architecture.md body), or null.</summary>
    public static string? ExtractC4(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return null;
        foreach (Match m in MermaidBlock().Matches(markdown))
        {
            var body = m.Groups[1].Value.Trim();
            if (body.StartsWith("C4", StringComparison.OrdinalIgnoreCase)) return body;
        }
        return null;
    }

    /// <summary>
    /// The impact of moving from <paramref name="beforeMd"/> to <paramref name="afterMd"/> architecture
    /// (each a markdown doc containing a C4 block). A null/absent "before" is treated as an empty model,
    /// so a brand-new architecture reports everything as added.
    /// </summary>
    public static string Between(string? beforeMd, string afterMd)
    {
        var afterC4 = ExtractC4(afterMd);
        if (afterC4 is null) return "No C4 architecture found in the proposed document.";

        var after = Mermaid.ParseAndLayoutC4(afterC4)?.Model;
        if (after is null) return "Could not parse the proposed C4 architecture.";

        var beforeC4 = beforeMd is null ? null : ExtractC4(beforeMd);
        var before = beforeC4 is null
            ? new C4Model()
            : (Mermaid.ParseAndLayoutC4(beforeC4)?.Model ?? new C4Model());

        return C4Diff.FormatImpact(C4Diff.Compare(before, after));
    }
}
