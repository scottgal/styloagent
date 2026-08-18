using System.Text.Json;

namespace Styloagent.Core.Mcp;

/// <summary>
/// Dynamically discovers the model catalog for the Codex runtime, so <c>agent_capabilities</c> reflects the
/// models actually available on this machine rather than a hard-coded list.
///
/// The codex CLI already maintains its own catalog at <c>~/.codex/models_cache.json</c> (slug, display name,
/// supported reasoning levels), so this reads that file rather than spawning a process — cheap enough to
/// refresh often and safe to call while the fleet is busy. A hard-coded list had drifted to
/// <c>gpt-5-codex</c>/<c>gpt-5</c>, models that no longer exist, while the operator's real default was
/// <c>gpt-5.6-terra</c>; since <see cref="AgentCapabilities.Supports"/> gates every spawn, that drift made
/// launching a codex agent on a real model impossible.
///
/// Results are cached for <see cref="CacheTtl"/>. Best-effort throughout: any failure yields an empty list so
/// callers fall back to the static defaults and codex stays selectable.
/// </summary>
public static class CodexModelDiscovery
{
    /// <summary>How long a discovered catalog stays fresh before the next call re-reads it.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = true };

    private static readonly object Gate = new();
    private static IReadOnlyList<AgentCapability> _cache = Array.Empty<AgentCapability>();
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>True when a successful discovery populated the cache.</summary>
    public static bool HasDiscovered { get; private set; }

    /// <summary>The codex CLI's own model cache — the machine's live catalog.</summary>
    public static string DefaultCatalogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "models_cache.json");

    /// <summary>
    /// The last discovered catalog (empty when discovery has never succeeded). Safe to call on the UI
    /// thread — it never touches the filesystem.
    /// </summary>
    public static IReadOnlyList<AgentCapability> CachedModels()
    {
        lock (Gate) return _cache;
    }

    /// <summary>
    /// Parses a codex <c>models_cache.json</c> document into capability entries, newest-CLI shape:
    /// <c>models[] = { slug, display_name, visibility, supported_reasoning_levels[] = { effort } }</c>.
    /// Models marked <c>visibility: "hide"</c> are internal and never offered. Returns an empty list for
    /// anything unparseable, so the caller keeps its static defaults.
    /// </summary>
    public static IReadOnlyList<AgentCapability> ParseCatalog(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<AgentCapability>();
        try
        {
            using var doc = JsonDocument.Parse(json, ParseOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
                return Array.Empty<AgentCapability>();

            var discovered = new List<AgentCapability>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object) continue;
                if (Str(model, "slug") is not { Length: > 0 } slug) continue;
                // "hide" marks internal/companion models (auto-review, watermarked variants) that the CLI
                // does not list for selection — offering them would just produce spawn failures.
                if (Str(model, "visibility") is { } vis && vis.Equals("hide", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Never let a real model shadow the CLI-default sentinel added below.
                if (slug.Equals("default", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(slug)) continue;

                discovered.Add(new AgentCapability(slug, Str(model, "display_name") ?? slug, Efforts(model)));
            }

            // No real models means no usable catalog — fall back rather than offer a bare "default".
            if (discovered.Count == 0) return Array.Empty<AgentCapability>();

            // Every runtime advertises "default" (use whatever the CLI is configured for) as the first choice.
            var all = new List<AgentCapability>(discovered.Count + 1)
            {
                new("default", "CLI default", UnionOfEfforts(discovered)),
            };
            all.AddRange(discovered);
            return all;
        }
        catch (JsonException) { return Array.Empty<AgentCapability>(); }
    }

    /// <summary>Re-reads the catalog now and caches the result. Returns the (possibly empty) catalog.</summary>
    public static async Task<IReadOnlyList<AgentCapability>> RefreshAsync(
        string? catalogPath = null, CancellationToken ct = default)
    {
        var models = await DiscoverAsync(catalogPath ?? DefaultCatalogPath, ct).ConfigureAwait(false);
        lock (Gate)
        {
            _cache = models;
            _cachedAt = DateTimeOffset.UtcNow;
            HasDiscovered = models.Count > 0;
        }
        return models;
    }

    /// <summary>
    /// Returns the cached catalog, triggering a background refresh when it is stale or never populated.
    /// Callers on a UI thread get the stale/empty list instantly — the file read never blocks the UI.
    /// </summary>
    public static IReadOnlyList<AgentCapability> GetOrStartRefresh()
    {
        lock (Gate)
        {
            if (HasDiscovered && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cache;
        }
        _ = Task.Run(() => RefreshAsync());
        return CachedModels();
    }

    private static async Task<IReadOnlyList<AgentCapability>> DiscoverAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<AgentCapability>();
            return ParseCatalog(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { return Array.Empty<AgentCapability>(); }
        catch (IOException) { return Array.Empty<AgentCapability>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<AgentCapability>(); }
    }

    /// <summary>The model's reasoning levels, always including the CLI-default sentinel effort.</summary>
    private static IReadOnlyList<string> Efforts(JsonElement model)
    {
        var efforts = new List<string> { "default" };
        if (model.TryGetProperty("supported_reasoning_levels", out var levels)
            && levels.ValueKind == JsonValueKind.Array)
        {
            foreach (var level in levels.EnumerateArray())
            {
                var name = level.ValueKind == JsonValueKind.String ? level.GetString() : Str(level, "effort");
                if (name is { Length: > 0 } && !efforts.Contains(name, StringComparer.OrdinalIgnoreCase))
                    efforts.Add(name);
            }
        }
        return efforts;
    }

    /// <summary>"default" accepts any effort some real model accepts — it resolves to a real model at launch.</summary>
    private static IReadOnlyList<string> UnionOfEfforts(IEnumerable<AgentCapability> models)
    {
        var union = new List<string> { "default" };
        foreach (var effort in models.SelectMany(m => m.Efforts))
            if (!union.Contains(effort, StringComparer.OrdinalIgnoreCase))
                union.Add(effort);
        return union;
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
