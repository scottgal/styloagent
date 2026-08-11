namespace Styloagent.Core.Model;

/// <summary>
/// A portable model CLASSIFICATION — agents store which tier they need, not a concrete model id. The
/// cockpit resolves <c>(runtime, tier)</c> → a concrete model via <see cref="ModelTierResolver"/>, so a
/// fleet is runtime-agnostic: an "opus-tier" architect uses DeepSeek V4 Pro on kilo, Opus on Claude,
/// GPT-5 on Codex — and switching runtimes never touches every agent's config. Effort is deliberately
/// NOT stored (it stays at the agent's discretion, per cockpit policy).
/// </summary>
public enum ModelTier
{
    /// <summary>The runtime's default model (no classification chosen).</summary>
    Default,

    /// <summary>Strongest reasoning — overviews / architecture / gnarly debugging.</summary>
    Opus,

    /// <summary>Standard — routine implementation, spawned specialists.</summary>
    Sonnet,

    /// <summary>Cheap + fast — docs, routine tests, quick reads.</summary>
    Haiku,
}

/// <summary>Centralized tier name mapping — mirrors <see cref="AgentRuntime"/>.</summary>
public static class ModelTierNames
{
    public static string Name(ModelTier tier) => tier switch
    {
        ModelTier.Opus => "opus",
        ModelTier.Sonnet => "sonnet",
        ModelTier.Haiku => "haiku",
        _ => "default",
    };

    public static ModelTier Parse(string? name) =>
        string.Equals(name, "opus", StringComparison.OrdinalIgnoreCase) ? ModelTier.Opus :
        string.Equals(name, "sonnet", StringComparison.OrdinalIgnoreCase) ? ModelTier.Sonnet :
        string.Equals(name, "haiku", StringComparison.OrdinalIgnoreCase) ? ModelTier.Haiku :
        ModelTier.Default;
}

/// <summary>
/// THE one source of truth mapping a <see cref="ModelTier"/> to a concrete model id for a runtime.
/// Every launch resolves through here — no scattered hard-coded model ids.
/// </summary>
public static class ModelTierResolver
{
    /// <summary>Resolves the concrete model id for <paramref name="tier"/> on <paramref name="runtime"/>,
    /// or null for <see cref="ModelTier.Default"/> (the runtime's own default applies).</summary>
    public static string? ResolveModel(AgentRuntimeKind runtime, ModelTier tier) => tier switch
    {
        ModelTier.Opus => runtime switch
        {
            AgentRuntimeKind.Claude => "opus",
            AgentRuntimeKind.Codex => "gpt-5",
            AgentRuntimeKind.Kilo => "deepseek/deepseek-v4-pro",
            _ => "deepseek-v4-pro",   // ClaudeDeepSeek — direct DeepSeek id (no [1m] suffix; the API rejects it)
        },
        ModelTier.Sonnet => runtime switch
        {
            AgentRuntimeKind.Claude => "sonnet",
            AgentRuntimeKind.Codex => "gpt-5-codex",
            AgentRuntimeKind.Kilo => "deepseek/deepseek-v4-flash",
            _ => "deepseek-v4-flash",
        },
        ModelTier.Haiku => runtime switch
        {
            AgentRuntimeKind.Claude => "haiku",
            AgentRuntimeKind.Codex => "gpt-5-codex",
            AgentRuntimeKind.Kilo => "deepseek/deepseek-v4-flash",
            _ => "deepseek-v4-flash",
        },
        _ => null,
    };

    /// <summary>All tiers a spawner can pick from (in display order).</summary>
    public static readonly ModelTier[] Tiers = { ModelTier.Opus, ModelTier.Sonnet, ModelTier.Haiku };

    /// <summary>
    /// The tier one step BELOW <paramref name="tier"/> — the standing rule for a spawned child: it runs the
    /// same runtime as the agent that spawned it, one tier down for the model. Expressing the step in tiers
    /// (not model ids) keeps it true on every runtime, and means job-type policy never has to name a CLI or
    /// a concrete model — naming those is what let policy override an explicit human request.
    /// <see cref="ModelTier.Haiku"/> is the floor, so a deep spawn tree never underflows;
    /// <see cref="ModelTier.Default"/> means "the runtime's own flagship", so one step down from it is
    /// <see cref="ModelTier.Sonnet"/>.
    /// </summary>
    public static ModelTier StepDown(ModelTier tier) => tier switch
    {
        ModelTier.Opus => ModelTier.Sonnet,
        ModelTier.Sonnet => ModelTier.Haiku,
        ModelTier.Haiku => ModelTier.Haiku,
        _ => ModelTier.Sonnet,
    };
}
