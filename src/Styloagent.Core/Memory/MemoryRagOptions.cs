namespace Styloagent.Core.Memory;

/// <summary>Project-local configuration for the disposable memory retrieval index.</summary>
public sealed record MemoryRagOptions(
    string Root,
    string IndexPath,
    string OllamaEndpoint,
    string EmbeddingModel,
    int MaxInjectedBytes = 6144,
    int DefaultLimit = 8)
{
    public static MemoryRagOptions Read(string projectRoot, string configPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(configPath))
            foreach (var raw in File.ReadLines(configPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var split = line.IndexOf(':');
                if (split <= 0) continue;
                values[line[..split].Trim()] = line[(split + 1)..].Trim().Trim('"', '\'');
            }

        string Resolve(string value, string fallback)
        {
            var path = values.GetValueOrDefault(value, fallback);
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(projectRoot, ".styloagent", path));
        }

        return new MemoryRagOptions(
            Resolve("root", "memory"),
            Resolve("index", "memory-rag.index.json"),
            values.GetValueOrDefault("ollamaEndpoint", "http://192.168.0.15:11434").TrimEnd('/'),
            values.GetValueOrDefault("embeddingModel", "nomic-embed-text"),
            Parse(values, "maxInjectedBytes", 6144, 1024, 32 * 1024),
            Parse(values, "defaultLimit", 8, 1, 20));
    }

    private static int Parse(IReadOnlyDictionary<string, string> values, string name, int fallback, int min, int max)
        => int.TryParse(values.GetValueOrDefault(name), out var value) ? Math.Clamp(value, min, max) : fallback;
}
