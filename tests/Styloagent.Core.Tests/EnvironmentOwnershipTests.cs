using Styloagent.Core.Environments;
using Xunit;

namespace Styloagent.Core.Tests;

public sealed class EnvironmentOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "env-" + Guid.NewGuid().ToString("N"));

    public EnvironmentOwnershipTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(EnvironmentRegistry.PolicyFile(_root), "controlOwner: overview-\n");
        var created = EnvironmentRegistry.Create(_root, "staging", "Staging", "non-production", "overview-");
        Assert.True(created.Success, created.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Offer_does_not_transfer_until_recipient_accepts()
    {
        var service = new EnvironmentOwnershipService(_root);
        Assert.True(service.Offer("overview-", "staging", "deploy-", "own releases", T(1)).Success);

        var pending = Assert.Single(EnvironmentOwnershipStore.Read(_root).Environments);
        Assert.Equal("overview-", pending.Owner);
        Assert.Equal("deploy-", pending.PendingOwner);
        Assert.False(service.Accept("test-", "staging", T(2)).Success);

        Assert.True(service.Accept("deploy-", "staging", T(3)).Success);
        var accepted = Assert.Single(EnvironmentOwnershipStore.Read(_root).Environments);
        Assert.Equal("deploy-", accepted.Owner);
        Assert.Null(accepted.PendingOwner);
    }

    [Fact]
    public void Only_control_owner_can_assign_or_revoke()
    {
        var service = new EnvironmentOwnershipService(_root);
        Assert.False(service.Assign("test-", "staging", "test-", "seize", T(1)).Success);
        Assert.True(service.Assign("overview-", "staging", "deploy-", "delegate", T(2)).Success);
        Assert.False(service.Revoke("test-", "staging", "seize", true, T(3)).Success);
        Assert.True(service.Revoke("overview-", "staging", "incident", true, T(4)).Success);

        var state = Assert.Single(EnvironmentOwnershipStore.Read(_root).Environments);
        Assert.Equal("overview-", state.Owner);
        Assert.True(state.History[^1].Force);
    }

    [Fact]
    public void Current_owner_can_return_to_fallback()
    {
        var service = new EnvironmentOwnershipService(_root);
        service.Assign("overview-", "staging", "deploy-", "delegate", T(1));
        Assert.True(service.Return("deploy-", "staging", "done", T(2)).Success);
        Assert.Equal("overview-", Assert.Single(EnvironmentOwnershipStore.Read(_root).Environments).Owner);
    }

    [Fact]
    public void Definition_rejects_path_traversal_ids()
    {
        Assert.False(EnvironmentRegistry.Create(_root, "../prod", "Production", "production", "overview-").Success);
    }

    [Fact]
    public void Browser_configuration_accepts_origins_and_secret_references_but_not_values()
    {
        Assert.False(EnvironmentRegistry.ConfigureBrowser(_root, "staging", "https://user:pass@example.test",
            null, 2, 1).Success);
        Assert.False(EnvironmentRegistry.ConfigureBrowser(_root, "staging", "https://staging.example.test",
            "raw-api-key", 2, 1).Success);
        Assert.True(EnvironmentRegistry.ConfigureBrowser(_root, "staging", "https://staging.example.test",
            "keychain://styloagent/staging", 3, 1).Success);

        var definition = Assert.Single(EnvironmentRegistry.Read(_root));
        Assert.Equal("https://staging.example.test", definition.Targets.WebOrigin);
        Assert.Equal("keychain://styloagent/staging", definition.Targets.BrowserCredentialRef);
        Assert.Equal(3, definition.Capacity.BrowserRead);
    }

    private static DateTimeOffset T(int second) => new(2026, 7, 22, 10, 0, second, TimeSpan.Zero);
}
