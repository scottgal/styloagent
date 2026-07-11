using System;
using System.IO;
using Styloagent.Core.Router;
using Xunit;

public class RouterProjectionTests
{
    [Fact]
    public void Missing_root_is_empty()
        => Assert.Empty(RouterProjection.Read(Path.Combine(Path.GetTempPath(), "no-" + Guid.NewGuid().ToString("N"))).Resources);

    [Fact]
    public void Reads_an_account_with_a_claim_a_grant_and_attempts()
    {
        var root = Path.Combine(Path.GetTempPath(), "router-" + Guid.NewGuid().ToString("N"));
        var acct = Path.Combine(root, "prod", "accounts", "deploy");
        Directory.CreateDirectory(Path.Combine(acct, "claims"));
        Directory.CreateDirectory(Path.Combine(acct, "grants"));
        File.WriteAllText(Path.Combine(acct, "resource.yaml"), "capacity: 1\nlockout:\n  budget: 5\n  window: 10m\n  cooldown: 15m\n");
        File.WriteAllText(Path.Combine(acct, "claims", "2026-07-11T120003Z-foss-.md"),
            "**From:** foss-\n**Timestamp:** 2026-07-11T12:00:03Z\n**Purpose:** deploy\n");
        File.WriteAllText(Path.Combine(acct, "grants", "docs-.md"),
            "**Holder:** docs-\n**Granted:** 2026-07-11T12:00:04Z\n**Expires:** 2026-07-11T12:02:04Z\n**ClaimTimestamp:** 2026-07-11T12:00:01Z\n");
        File.WriteAllText(Path.Combine(acct, "attempts.md"), "2026-07-11T12:00:05Z ok\n2026-07-11T12:03:11Z fail\n");
        try
        {
            var state = RouterProjection.Read(root);
            var r = Assert.Single(state.Resources);
            Assert.Equal("prod", r.Env);
            Assert.Equal(ResourceKind.Account, r.Kind);
            Assert.Equal("deploy", r.Name);
            Assert.Equal(1, r.Policy.Capacity);
            Assert.Contains(r.Claims, c => c.Prefix == "foss-" && c.Purpose == "deploy");
            Assert.Contains(r.Grants, g => g.Prefix == "docs-");
            Assert.Equal(2, r.Attempts.Count);
            Assert.Contains(r.Attempts, a => !a.Ok);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
