using System.Net.Http.Json;
using System.Text;
using Styloagent.Core.Memory;

namespace Styloagent.Core.Retrieval;

public sealed record DocumentAnswer(string Markdown, IReadOnlyList<ContextHit> Sources, bool Synthesized);

/// <summary>Grounded local synthesis over document retrieval. The model receives only retrieved chunks.</summary>
public static class DocumentQuestionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public static async Task<DocumentAnswer> AnswerAsync(string projectRoot, MemoryRagOptions options, string question,
        IReadOnlyCollection<string>? sources = null, CancellationToken ct = default)
    {
        var requestedSources = sources is { Count: > 0 } ? sources : new[] { "docs" };
        var result = await ContextRetrievalService.RetrieveAsync(projectRoot, Path.Combine(projectRoot, ".styloagent", "channel"),
            Path.Combine(projectRoot, ".styloagent", "issues"), [], options, "", question, requestedSources, 8, 10_000, ct);
        if (result.Hits.Count == 0)
            return new DocumentAnswer("## No matching documents\n\nI could not find relevant project documentation.", [], false);

        var evidence = new StringBuilder();
        for (var i = 0; i < result.Hits.Count; i++)
            evidence.AppendLine($"[S{i + 1}] {result.Hits[i].Title} ({result.Hits[i].Path})\n{result.Hits[i].Content}\n");
        var prompt = $"""
You answer questions about this project's documents. Use only the supplied evidence. If it is insufficient, say so plainly. Be concise. Cite claims as [S1], [S2]. Do not invent paths, APIs, or decisions.

Question: {question}

Evidence:
{evidence}
""";
        try
        {
            using var response = await Http.PostAsJsonAsync(options.OllamaEndpoint + "/api/generate",
                new { model = options.SynthesisModel, prompt, stream = false, options = new { temperature = 0.1 } }, ct);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<OllamaGenerate>(cancellationToken: ct);
                if (!string.IsNullOrWhiteSpace(payload?.Response))
                    return new DocumentAnswer($"## {question}\n\n{payload.Response.Trim()}\n\n---\n### Sources\n{Sources(result.Hits)}", result.Hits, true);
            }
        }
        catch (Exception ex)
        {
            var why = ex is HttpRequestException or TaskCanceledException
                ? $"Ollama is not reachable at {options.OllamaEndpoint} — is it running?"
                : ex.Message;
            return new DocumentAnswer(
                $"## {question}\n\n_Synthesis unavailable: {why}_\n\nThese are the retrieved passages:\n\n{string.Join("\n\n", result.Hits.Select((h, i) => $"### [S{i + 1}] {h.Title}\n{h.Content}"))}\n\n---\n### Sources\n{Sources(result.Hits)}",
                result.Hits, false);
        }

        // Ollama responded but returned no text — degrade to retrieval-only.
        return new DocumentAnswer(
            $"## {question}\n\n_Synthesis returned empty — these are the retrieved passages._\n\n{string.Join("\n\n", result.Hits.Select((h, i) => $"### [S{i + 1}] {h.Title}\n{h.Content}"))}\n\n---\n### Sources\n{Sources(result.Hits)}",
            result.Hits, false);
    }

    private static string Sources(IReadOnlyList<ContextHit> hits) => string.Join("\n", hits.Select((h, i) => $"- [S{i + 1}] `{h.Path}` — {h.Title}"));
    private sealed record OllamaGenerate(string? Response);
}
