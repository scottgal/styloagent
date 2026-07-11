using Styloagent.Core.Channel;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

/// <summary>
/// Per-project mapping of a message's <see cref="MessagePriority"/> to the
/// <see cref="DeliveryMode"/> the recipient's runtime applies. Read from
/// <c>.styloagent/priority-policy.yaml</c>; the same message can interrupt in one project
/// and merely queue in a calmer one.
/// </summary>
public sealed record PriorityPolicy(
    DeliveryMode Urgent,
    DeliveryMode Normal,
    DeliveryMode Low,
    DeliveryMode Info)
{
    /// <summary>The shipped default ladder (see the design spec).</summary>
    public static PriorityPolicy Default => new(
        Urgent: DeliveryMode.Interrupt,
        Normal: DeliveryMode.NextPrompt,
        Low: DeliveryMode.Convenient,
        Info: DeliveryMode.Informational);

    /// <summary>Resolves the delivery mode for a message priority.</summary>
    public DeliveryMode ModeFor(MessagePriority priority) => priority switch
    {
        MessagePriority.Urgent => Urgent,
        MessagePriority.Normal => Normal,
        MessagePriority.Low    => Low,
        MessagePriority.Info   => Info,
        _ => Normal,
    };
}

/// <summary>YAML surface for <see cref="PriorityPolicy"/>: string mode names per level.</summary>
[YamlObject]
internal partial class PriorityPolicyFile
{
    public string? Urgent { get; set; }
    public string? Normal { get; set; }
    public string? Low { get; set; }
    public string? Info { get; set; }
}

/// <summary>Tolerant reader: defaults on missing/invalid, never throws.</summary>
public static class PriorityPolicyReader
{
    public static PriorityPolicy Read(string path)
    {
        var d = PriorityPolicy.Default;
        try
        {
            if (!File.Exists(path)) return d;
            var bytes = File.ReadAllBytes(path);
            var file = YamlSerializer.Deserialize<PriorityPolicyFile>(bytes);
            return new PriorityPolicy(
                Urgent: ParseMode(file.Urgent, d.Urgent),
                Normal: ParseMode(file.Normal, d.Normal),
                Low: ParseMode(file.Low, d.Low),
                Info: ParseMode(file.Info, d.Info));
        }
        catch { return d; }
    }

    /// <summary>Case-insensitive mode-name parse; unrecognized/empty falls back to <paramref name="fallback"/>.</summary>
    internal static DeliveryMode ParseMode(string? raw, DeliveryMode fallback) =>
        (raw?.Trim().ToLowerInvariant()) switch
        {
            "interrupt" => DeliveryMode.Interrupt,
            "nextprompt" or "next-prompt" or "next_prompt" => DeliveryMode.NextPrompt,
            "poll" => DeliveryMode.Poll,
            "convenient" => DeliveryMode.Convenient,
            "informational" or "info" => DeliveryMode.Informational,
            _ => fallback,
        };
}
