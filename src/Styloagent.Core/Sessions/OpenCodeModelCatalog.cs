using System.Collections.Concurrent;
using System.Text.Json;
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
