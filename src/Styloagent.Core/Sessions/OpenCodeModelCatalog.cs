using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Styloagent.Core.Mcp;

namespace Styloagent.Core.Sessions;

public sealed record OpenCodeModel(string Id, string ProviderId, string Name, IReadOnlyList<string> Variants, long ContextTokens)
{
    public string QualifiedId => ProviderId + "/" + Id;
    public AgentCapability Capability => new(QualifiedId, Name + " (" + ProviderId + ")", Variants.Prepend("default").Distinct(StringComparer.Ordinal).ToArray());
}

/// <summary>Only advertises models and variants confirmed by a running OpenCode backend.</summary>
public static class OpenCodeModelCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<OpenCodeModel>> Catalogs = new(StringComparer.Ordinal);
    public static IReadOnlyList<OpenCodeModel> For(string? repo) => repo is not null && Catalogs.TryGetValue(Path.GetFullPath(repo), out var models) ? models : [];
    public static void Update(string repo, IReadOnlyList<OpenCodeModel> models) => Catalogs[Path.GetFullPath(repo)] = models;

    /// <summary>Choose Flash from the native catalog, never an unrelated provider's global default.</summary>
    public static OpenCodeModel DefaultFlash(IReadOnlyList<OpenCodeModel> models, bool openRouter)
    {
        var direct = models.FirstOrDefault(m => m.ProviderId == "deepseek" && m.Id == DeepSeekModels.Flash);
        if (!openRouter && direct is not null) return direct;
        var routed = models.Where(m => m.ProviderId == "openrouter")
            .Select(m => (Model: m, Match: Regex.Match(m.Id,
                @"^deepseek/deepseek-v(?<version>\d+(?:\.\d+)*)-flash(?:-(?<date>\d{4}))?$", RegexOptions.CultureInvariant)))
            .Where(m => m.Match.Success)
            .Select(m =>
            {
                var value = m.Match.Groups["version"].Value;
                Version.TryParse(value.Contains('.') ? value : value + ".0", out var version);
                return (m.Model, Version: version, Date: int.TryParse(m.Match.Groups["date"].Value, out var date) ? date : 0);
            })
            .Where(m => m.Version is not null)
            .OrderByDescending(m => m.Version).ThenByDescending(m => m.Date).FirstOrDefault().Model;
        return routed ?? throw new ArgumentException("OpenCode does not advertise an enabled DeepSeek Flash model. Configure DeepSeek or OpenRouter credentials, or explicitly select another model.");
    }

    public static IReadOnlyList<OpenCodeModel> Parse(JsonElement root)
    {
        var result = new List<OpenCodeModel>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True ||
                !model.TryGetProperty("capabilities", out var caps) ||
                !caps.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.True ||
                !caps.TryGetProperty("output", out var output) || !output.EnumerateArray().Any(v => v.GetString() == "text")) continue;
            var variants = model.TryGetProperty("variants", out var list) ? list.EnumerateArray()
                .Select(v => v.GetProperty("id").GetString()!).ToArray() : Array.Empty<string>();
            var context = model.TryGetProperty("limit", out var limit) && limit.TryGetProperty("context", out var tokens) ? tokens.GetInt64() : 0;
            result.Add(new(model.GetProperty("id").GetString()!, model.GetProperty("providerID").GetString()!,
                model.GetProperty("name").GetString()!, variants, context));
        }
        return result;
    }
}
