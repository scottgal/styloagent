using System.Globalization;
using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Router;

/// <summary>Per-account lockout budget: after <see cref="Budget"/> failures within <see cref="Window"/>, cool for <see cref="Cooldown"/>.</summary>
public sealed record LockoutPolicy(int Budget, TimeSpan Window, TimeSpan Cooldown);

/// <summary>A resource's arbitration config. Capacity 1 = exclusive account; N = slot pool. Lockout null = untracked.</summary>
public sealed record ResourcePolicy(int Capacity, LockoutPolicy? Lockout, TimeSpan LeaseTtl)
{
    public static ResourcePolicy Default { get; } = new(Capacity: 1, Lockout: null, LeaseTtl: TimeSpan.FromMinutes(2));
}

[YamlObject]
internal partial class LockoutFile { public int? Budget { get; set; } public string? Window { get; set; } public string? Cooldown { get; set; } }

[YamlObject]
internal partial class ResourceFile { public int? Capacity { get; set; } public string? LeaseTtl { get; set; } public LockoutFile? Lockout { get; set; } }

/// <summary>Tolerant reader for <c>resource.yaml</c>. Missing/invalid → <see cref="ResourcePolicy.Default"/>.</summary>
public static class RouterPolicyReader
{
    public static ResourcePolicy Read(string resourceYamlPath)
    {
        var d = ResourcePolicy.Default;
        try
        {
            if (!File.Exists(resourceYamlPath)) return d;
            var f = YamlSerializer.Deserialize<ResourceFile>(File.ReadAllBytes(resourceYamlPath));
            LockoutPolicy? lockout = f.Lockout is null ? null : new LockoutPolicy(
                Budget: f.Lockout.Budget ?? 5,
                Window: ParseDuration(f.Lockout.Window, TimeSpan.FromMinutes(10)),
                Cooldown: ParseDuration(f.Lockout.Cooldown, TimeSpan.FromMinutes(15)));
            return new ResourcePolicy(
                Capacity: f.Capacity is > 0 ? f.Capacity.Value : 1,
                Lockout: lockout,
                LeaseTtl: ParseDuration(f.LeaseTtl, d.LeaseTtl));
        }
        catch { return d; }
    }

    /// <summary>Parses <c>"10m"</c>/<c>"90s"</c>/<c>"1h"</c> (or bare seconds) to a TimeSpan; fallback on any failure.</summary>
    public static TimeSpan ParseDuration(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        raw = raw.Trim();
        char unit = raw[^1];
        var numPart = char.IsDigit(unit) ? raw : raw[..^1];
        if (!double.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return fallback;
        return unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ when char.IsDigit(unit) => TimeSpan.FromSeconds(n),
            _ => fallback,
        };
    }
}
