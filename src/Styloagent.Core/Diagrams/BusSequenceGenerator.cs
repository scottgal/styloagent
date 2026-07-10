using System.Text;

namespace Styloagent.Core.Diagrams;

public sealed record SeqMessage(string From, DateTimeOffset? When);
public sealed record SeqThread(string Slug, IReadOnlyList<SeqMessage> Messages);

/// <summary>Renders bus threads as a mermaid flowchart (graph LR) of message flow. Pure, total.</summary>
public static class BusSequenceGenerator
{
    public static string Build(IEnumerable<SeqThread> threads)
    {
        var list = threads.ToList();
        var sb = new StringBuilder();
        sb.Append("# Bus Sequence\n\n```mermaid\ngraph LR\n");

        var senders = new List<string>();
        foreach (var t in list)
            foreach (var m in t.Messages)
                if (!string.IsNullOrWhiteSpace(m.From) && !senders.Contains(m.From)) senders.Add(m.From);

        if (senders.Count == 0)
        {
            sb.Append("    empty[\"no bus activity yet\"]\n```\n");
            return sb.ToString();
        }

        foreach (var s in senders)
            sb.Append($"    {SystemMapGenerator.Id(s)}[\"{SystemMapGenerator.Escape(s)}\"]\n");

        int awaiting = 0;
        var orderedThreads = list.OrderBy(t =>
            t.Messages.Select(m => m.When ?? DateTimeOffset.MaxValue).DefaultIfEmpty(DateTimeOffset.MaxValue).Min());
        foreach (var t in orderedThreads)
        {
            var chain = new List<string>();
            foreach (var m in t.Messages
                         .Where(m => !string.IsNullOrWhiteSpace(m.From))
                         .OrderBy(m => m.When ?? DateTimeOffset.MaxValue))
                if (chain.Count == 0 || chain[^1] != m.From) chain.Add(m.From);

            if (chain.Count == 0) continue;
            if (chain.Count == 1)
            {
                var aid = $"await{awaiting++}";
                sb.Append($"    {aid}[\"{SystemMapGenerator.Escape(chain[0])}: {SystemMapGenerator.Escape(t.Slug)} (awaiting reply)\"]\n");
                sb.Append($"    {SystemMapGenerator.Id(chain[0])} --> {aid}\n");
            }
            else
            {
                for (int i = 0; i + 1 < chain.Count; i++)
                    sb.Append($"    {SystemMapGenerator.Id(chain[i])} -->|{EdgeLabel(t.Slug)}| {SystemMapGenerator.Id(chain[i + 1])}\n");
            }
        }
        sb.Append("```\n");
        return sb.ToString();
    }

    // Mermaid edge labels can't contain '|' or '"'; keep it simple.
    private static string EdgeLabel(string slug) => slug.Replace("|", "/", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal);
}
