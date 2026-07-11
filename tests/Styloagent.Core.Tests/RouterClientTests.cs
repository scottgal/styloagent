using System;
using System.IO;
using Styloagent.Core.Router;
using Xunit;

public class RouterClientTests
{
    [Fact]
    public void DropClaim_writes_a_claim_the_projection_reads()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcl-" + Guid.NewGuid().ToString("N"));
        try
        {
            RouterClient.DropClaim(root, "prod", "deploy", "foss-", "ship it",
                new DateTimeOffset(2026, 7, 11, 12, 0, 3, TimeSpan.Zero));
            var state = RouterProjection.Read(root);
            var r = Assert.Single(state.Resources);
            Assert.Equal(ResourceKind.Account, r.Kind);
            Assert.Contains(r.Claims, c => c.Prefix == "foss-" && c.Purpose == "ship it");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LogAttempt_appends_and_Release_removes_grant()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcl-" + Guid.NewGuid().ToString("N"));
        try
        {
            RouterClient.LogAttempt(root, "prod", "deploy", ok: false, new DateTimeOffset(2026, 7, 11, 12, 0, 5, TimeSpan.Zero));
            var attempts = File.ReadAllText(RouterPaths.AttemptsFile(root, "prod", ResourceKind.Account, "deploy"));
            Assert.Contains("fail", attempts);

            RouterWriter.WriteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            RouterClient.Release(root, "prod", "deploy", "foss-");
            Assert.False(File.Exists(RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
