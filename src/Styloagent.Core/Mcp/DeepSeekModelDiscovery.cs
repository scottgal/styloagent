using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Styloagent.Core.Mcp;

public sealed record DiscoveredDeepSeekModel(AgentCapability Capability, long Created, int ContextLength);

/// <summary>Non-blocking, single-flight OpenRouter discovery with last-good results on failure.</summary>
public sealed class DeepSeekModelDiscovery(HttpClient client)
{
    private static readonly string[] DefaultEfforts = ["default"];
    public static DeepSeekModelDiscovery Shared { get; } = new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private IReadOnlyList<DiscoveredDeepSeekModel> _models = Array.Empty<DiscoveredDeepSeekModel>();
    private DateTimeOffset _attemptedAt = DateTimeOffset.MinValue;
    private Task? _refresh;

    public IReadOnlyList<DiscoveredDeepSeekModel> CachedModels { get { lock (_gate) return _models; } }

    public IReadOnlyList<DiscoveredDeepSeekModel> GetOrStartRefresh(string? apiKey)
    {
        lock (_gate)
        {
            if ((_refresh is null || _refresh.IsCompleted) && DateTimeOffset.UtcNow - _attemptedAt >= CacheTtl)
            {
                _attemptedAt = DateTimeOffset.UtcNow;
                _refresh = Task.Run(() => RefreshAsync(apiKey));
            }
            return _models;
        }
    }

    public async Task RefreshAsync(string? apiKey, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var models = ParseCatalog(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (models.Count > 0)
                lock (_gate) _models = models;
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
    }

    public static IReadOnlyList<DiscoveredDeepSeekModel> ParseCatalog(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DiscoveredDeepSeekModel>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<DiscoveredDeepSeekModel>();
            var result = new List<DiscoveredDeepSeekModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || Str(model, "id") is not { } id ||
                    !id.StartsWith("deepseek/", StringComparison.Ordinal) || id.EndsWith(":batch", StringComparison.Ordinal) || seen.Contains(id)) continue;
                // Coding agents require text output and tool calling; do not offer embedding-only models.
                if (!model.TryGetProperty("supported_parameters", out var parameters) || !Contains(parameters, "tools") ||
                    !model.TryGetProperty("architecture", out var architecture) || architecture.ValueKind != JsonValueKind.Object ||
                    !architecture.TryGetProperty("output_modalities", out var output) || !Contains(output, "text")) continue;
                // Capability flags do not enumerate accepted effort values. OpenCode's native
                // model catalog supplies exact variants; generic OpenRouter discovery stays default-only.
                var efforts = DefaultEfforts;
                seen.Add(id);
                result.Add(new(new(id, (Str(model, "name") ?? id) + " (OpenRouter)", efforts),
                    Number(model, "created"), (int)Math.Clamp(Number(model, "context_length"), 0, int.MaxValue)));
            }
            return result.OrderByDescending(m => m.Created).ThenBy(m => m.Capability.Id, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { return Array.Empty<DiscoveredDeepSeekModel>(); }
    }

    /// <summary>Only stable Flash releases can replace the automatic Flash default.</summary>
    public static string LatestFlash(IReadOnlyList<DiscoveredDeepSeekModel> models)
        => models.Where(m => Regex.IsMatch(m.Capability.Id, @"^deepseek/deepseek-v\d+(\.\d+)*-flash(-\d{4})?$",
                RegexOptions.CultureInvariant))
            .OrderByDescending(m => m.Created).ThenBy(m => m.Capability.Id, StringComparer.Ordinal)
            .FirstOrDefault()?.Capability.Id ?? Sessions.DeepSeekModels.OpenRouterFlash;

    private static bool Contains(JsonElement array, string value)
        => array.ValueKind == JsonValueKind.Array && array.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == value);
    private static string? Str(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Number(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
