using System;
using System.IO;
using Styloagent.Core.Router;
using Xunit;

public class RouterWriterTests
{
    [Fact]
    public void WriteGrant_then_DeleteGrant_round_trips()
    {
        var root = Path.Combine(Path.GetTempPath(), "rw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var granted = new DateTimeOffset(2026, 7, 11, 12, 0, 4, TimeSpan.Zero);
            RouterWriter.WriteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-",
                granted, granted + TimeSpan.FromMinutes(2), granted);
            var file = RouterPaths.GrantFile(root, "prod", ResourceKind.Account, "deploy", "foss-");
            Assert.True(File.Exists(file));
            Assert.Contains("**Holder:** foss-", File.ReadAllText(file));

            RouterWriter.DeleteGrant(root, "prod", ResourceKind.Account, "deploy", "foss-");
            Assert.False(File.Exists(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AppendLog_creates_and_appends()
    {
        var root = Path.Combine(Path.GetTempPath(), "rw-" + Guid.NewGuid().ToString("N"));
        try
        {
            RouterWriter.AppendLog(root, "prod", ResourceKind.Slot, "ci", "granted foss-");
            var log = File.ReadAllText(RouterPaths.LogFile(root, "prod", ResourceKind.Slot, "ci"));
            Assert.Contains("granted foss-", log);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
