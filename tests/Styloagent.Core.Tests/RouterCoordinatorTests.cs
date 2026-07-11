using System;
using System.IO;
using Styloagent.Core.Router;
using Xunit;

public class RouterCoordinatorTests
{
    [Fact]
    public void Tick_grants_a_pending_claim()
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        var claims = RouterPaths.ClaimsDir(root, "prod", ResourceKind.Account, "deploy");
        Directory.CreateDirectory(claims);
        File.WriteAllText(RouterPaths.ResourceDir(root, "prod", ResourceKind.Account, "deploy") + "/resource.yaml", "capacity: 1\n");
        File.WriteAllText(Path.Combine(claims, "2026-07-11T120003Z-foss-.md"),
            "**From:** foss-\n**Timestamp:** 2026-07-11T12:00:03Z\n**Purpose:** deploy\n");
        try
        {
            var applied = RouterCoordinator.Tick(root, new DateTimeOffset(2026, 7, 11, 12, 0, 10, TimeSpan.Zero));
            Assert.Contains(applied, d => d.Action == RouterAction.Grant && d.Prefix == "foss-");
            Assert.True(File.Exists(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Tick_expires_a_stale_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-" + Guid.NewGuid().ToString("N"));
        RouterWriter.WriteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-",
            new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 11, 12, 2, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));
        File.WriteAllText(RouterPaths.ResourceDir(root, "prod", ResourceKind.Account, "deploy") + "/resource.yaml", "capacity: 1\nleaseTtl: 2m\n");
        // Force the grant file's mtime far in the past so the lease is stale.
        File.SetLastWriteTimeUtc(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-"),
            new DateTime(2026, 7, 11, 11, 0, 0, DateTimeKind.Utc));
        try
        {
            var applied = RouterCoordinator.Tick(root, new DateTimeOffset(2026, 7, 11, 12, 0, 10, TimeSpan.Zero));
            Assert.Contains(applied, d => d.Action == RouterAction.Expire && d.Prefix == "foss-");
            Assert.False(File.Exists(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
