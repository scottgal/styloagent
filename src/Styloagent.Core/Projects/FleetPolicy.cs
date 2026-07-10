using VYaml.Annotations;
using VYaml.Serialization;

namespace Styloagent.Core.Projects;

/// <summary>Fleet guardrail limits (read from .styloagent/fleet.yaml).</summary>
public sealed record FleetPolicy(int MaxFleet, int MaxDepth)
{
    public static FleetPolicy Default => new(12, 3);
}

[YamlObject]
internal partial class FleetPolicyFile
{
    public int MaxFleet { get; set; } = 12;
    public int MaxDepth { get; set; } = 3;
}

/// <summary>Tolerant reader: defaults on missing/invalid, never throws.</summary>
public static class FleetPolicyReader
{
    public static FleetPolicy Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return FleetPolicy.Default;
            var bytes = File.ReadAllBytes(path);
            var file = YamlSerializer.Deserialize<FleetPolicyFile>(bytes);
            int maxFleet = file.MaxFleet > 0 ? file.MaxFleet : 12;
            int maxDepth = file.MaxDepth > 0 ? file.MaxDepth : 3;
            return new FleetPolicy(maxFleet, maxDepth);
        }
        catch { return FleetPolicy.Default; }
    }
}
