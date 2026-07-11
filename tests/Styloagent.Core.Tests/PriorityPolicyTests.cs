using Styloagent.Core.Channel;
using Styloagent.Core.Projects;
using Xunit;

public class PriorityPolicyTests
{
    [Fact]
    public void Default_maps_the_shipped_ladder()
    {
        var p = PriorityPolicy.Default;
        Assert.Equal(DeliveryMode.Interrupt, p.ModeFor(MessagePriority.Urgent));
        Assert.Equal(DeliveryMode.NextPrompt, p.ModeFor(MessagePriority.Normal));
        Assert.Equal(DeliveryMode.Convenient, p.ModeFor(MessagePriority.Low));
        Assert.Equal(DeliveryMode.Informational, p.ModeFor(MessagePriority.Info));
    }

    [Fact]
    public void Reader_returns_default_when_file_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".yaml");
        Assert.Equal(PriorityPolicy.Default, PriorityPolicyReader.Read(path));
    }

    [Fact]
    public void Reader_parses_yaml_and_overrides_per_level()
    {
        var path = Path.Combine(Path.GetTempPath(), "prio-policy-" + Guid.NewGuid().ToString("N") + ".yaml");
        // This project is calmer: urgent only queues, low is push-nothing.
        File.WriteAllText(path, "urgent: nextprompt\nnormal: convenient\nlow: informational\ninfo: informational\n");
        try
        {
            var p = PriorityPolicyReader.Read(path);
            Assert.Equal(DeliveryMode.NextPrompt, p.ModeFor(MessagePriority.Urgent));
            Assert.Equal(DeliveryMode.Convenient, p.ModeFor(MessagePriority.Normal));
            Assert.Equal(DeliveryMode.Informational, p.ModeFor(MessagePriority.Low));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_falls_back_per_field_on_unrecognized_mode()
    {
        var path = Path.Combine(Path.GetTempPath(), "prio-policy-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, "urgent: explode\n");   // garbage → keep default for urgent, defaults elsewhere
        try
        {
            var p = PriorityPolicyReader.Read(path);
            Assert.Equal(DeliveryMode.Interrupt, p.ModeFor(MessagePriority.Urgent));  // default retained
            Assert.Equal(DeliveryMode.NextPrompt, p.ModeFor(MessagePriority.Normal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProjectConfig_exposes_priority_policy_path()
    {
        var cfg = ProjectConfig.For("/tmp/proj");
        Assert.Equal(Path.Combine("/tmp/proj", ".styloagent", "priority-policy.yaml"), cfg.PriorityPolicyPath);
    }
}
