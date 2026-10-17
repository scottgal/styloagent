namespace Styloagent.Core.Sessions;

/// <summary>Provider-specific identifiers for the current DeepSeek Flash release.</summary>
public static class DeepSeekModels
{
    public const string Flash = "deepseek-flash";
    public const string OpenRouterFlash = "deepseek/deepseek-v4.1-flash-1017";

    public static bool IsOpenRouter(string? baseUrl)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
           uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    public static bool IsCompatible(string model, string? baseUrl)
        => model == "default" || (IsOpenRouter(baseUrl)
            ? model is Flash or "deepseek-v4-flash" || model.Contains('/', StringComparison.Ordinal)
            : !model.Contains('/', StringComparison.Ordinal));

    /// <summary>Translate the direct API's Flash aliases when routing through OpenRouter.</summary>
    public static string ResolveForEndpoint(string model, string? baseUrl, string? latestFlash = null)
    {
        if (!IsCompatible(model, baseUrl))
            throw new ArgumentException($"Model '{model}' is incompatible with the configured DeepSeek endpoint. Select a model from agent_capabilities.", nameof(model));
        if (model == "default") model = Flash;
        if (IsOpenRouter(baseUrl))
        {
            return model switch
            {
                Flash or "deepseek-v4-flash" => latestFlash ?? OpenRouterFlash,
                _ => model,
            };
        }
        return model;
    }
}
