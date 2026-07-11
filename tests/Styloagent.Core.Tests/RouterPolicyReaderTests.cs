using System;
using Styloagent.Core.Router;
using Xunit;

public class RouterPolicyReaderTests
{
    [Theory]
    [InlineData("10m", 600)]
    [InlineData("90s", 90)]
    [InlineData("1h", 3600)]
    [InlineData("bad", 42)]     // falls back
    public void ParseDuration_reads_suffixes(string raw, int expectedSeconds)
    {
        var ts = RouterPolicyReader.ParseDuration(raw, TimeSpan.FromSeconds(42));
        Assert.Equal(expectedSeconds, (int)ts.TotalSeconds);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var p = RouterPolicyReader.Read(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-" + Guid.NewGuid().ToString("N") + ".yaml"));
        Assert.Equal(1, p.Capacity);
        Assert.Null(p.Lockout);
        Assert.Equal(TimeSpan.FromMinutes(2), p.LeaseTtl);
    }

    [Fact]
    public void Reads_capacity_lockout_and_lease()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "res-" + Guid.NewGuid().ToString("N") + ".yaml");
        System.IO.File.WriteAllText(file,
            "capacity: 3\nleaseTtl: 90s\nlockout:\n  budget: 5\n  window: 10m\n  cooldown: 15m\n");
        try
        {
            var p = RouterPolicyReader.Read(file);
            Assert.Equal(3, p.Capacity);
            Assert.Equal(TimeSpan.FromSeconds(90), p.LeaseTtl);
            Assert.NotNull(p.Lockout);
            Assert.Equal(5, p.Lockout!.Budget);
            Assert.Equal(TimeSpan.FromMinutes(10), p.Lockout.Window);
            Assert.Equal(TimeSpan.FromMinutes(15), p.Lockout.Cooldown);
        }
        finally { System.IO.File.Delete(file); }
    }
}
