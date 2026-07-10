using Styloagent.Core.Projects;
using Xunit;

namespace Styloagent.Core.Tests;

public class FleetPolicyReaderTests
{
    [Fact]
    public void Reads_a_valid_policy()
    {
        var path = Path.Combine(Path.GetTempPath(), "fleet-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, "maxFleet: 6\nmaxDepth: 2\n");
        try
        {
            var p = FleetPolicyReader.Read(path);
            Assert.Equal(6, p.MaxFleet);
            Assert.Equal(2, p.MaxDepth);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Returns_defaults_for_missing_or_invalid()
    {
        var missing = FleetPolicyReader.Read(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(FleetPolicy.Default, missing);

        var bad = Path.Combine(Path.GetTempPath(), "bad-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(bad, "this: [is not: valid");
        try { Assert.Equal(FleetPolicy.Default, FleetPolicyReader.Read(bad)); }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void Scaffolder_writes_fleet_yaml_and_does_not_overwrite_edits()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = ProjectScaffolder.Ensure(root);
            Assert.True(File.Exists(cfg.FleetPolicyPath));

            File.WriteAllText(cfg.FleetPolicyPath, "maxFleet: 3\nmaxDepth: 1\n");
            ProjectScaffolder.Ensure(root);   // second call must not clobber
            Assert.Equal(3, FleetPolicyReader.Read(cfg.FleetPolicyPath).MaxFleet);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
