using System;
using System.IO;
using System.Threading;
using Styloagent.App.Router;
using Styloagent.Core.Router;
using Xunit;

namespace Styloagent.App.Tests;

/// <summary>
/// Integration test for <see cref="RouterHost"/>.  Builds a temp ledger with a pending claim,
/// starts a host, and asserts a Grant decision fires within the timer interval (2 s + 4 s buffer).
/// </summary>
public class RouterHostTests : IDisposable
{
    private readonly string _root;

    public RouterHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rh-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void RouterHost_fires_grant_decision_for_pending_claim()
    {
        // Arrange: seed a pending claim (mirrors RouterCoordinatorTests setup).
        var claims = RouterPaths.ClaimsDir(_root, "prod", ResourceKind.Account, "deploy");
        Directory.CreateDirectory(claims);
        File.WriteAllText(
            RouterPaths.ResourceDir(_root, "prod", ResourceKind.Account, "deploy") + "/resource.yaml",
            "capacity: 1\n");
        File.WriteAllText(
            Path.Combine(claims, "2026-07-11T120003Z-foss-.md"),
            "**From:** foss-\n**Timestamp:** 2026-07-11T12:00:03Z\n**Purpose:** deploy\n");

        var mre = new ManualResetEventSlim(false);
        RouterDecision? received = null;

        // Act: start host; timer fires at dueTime=0, so first tick is immediate.
        using var host = new RouterHost(_root, d =>
        {
            if (d.Action == RouterAction.Grant)
            {
                received = d;
                mre.Set();
            }
        });

        // Assert: Grant fires within 6 s (timer dueTime=0 means first tick is nearly immediate).
        var signalled = mre.Wait(TimeSpan.FromSeconds(6));
        Assert.True(signalled, "Timed out waiting for Grant decision from RouterHost");
        Assert.NotNull(received);
        Assert.Equal(RouterAction.Grant, received!.Action);
        Assert.Equal("foss-", received.Prefix);
    }

    [Fact]
    public void RouterHost_disposes_cleanly_without_throwing()
    {
        // Arrange: root that doesn't exist yet — host must be tolerant.
        var missingRoot = Path.Combine(Path.GetTempPath(), "rh-missing-" + Guid.NewGuid().ToString("N"));

        var host = new RouterHost(missingRoot, _ => { });

        // Act + Assert: dispose must not throw even if root never existed.
        var ex = Record.Exception(() => host.Dispose());
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
