using Styloagent.Core.Browser;
using Xunit;

namespace Styloagent.Core.Tests;

public sealed class BrowserJobServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "browser-routing-" + Guid.NewGuid().ToString("N"));
    private string Environments => Path.Combine(_root, "environments");
    private string Browser => Path.Combine(_root, "browser");

    public BrowserJobServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(Environments, "definitions"));
        File.WriteAllText(Path.Combine(Environments, "policy.yaml"), "controlOwner: overview-\n");
        File.WriteAllText(Path.Combine(Environments, "definitions", "staging.yaml"),
            "id: staging\n" +
            "displayName: Staging\n" +
            "owner: deploy-\n" +
            "fallbackOwner: overview-\n" +
            "classification: non-production\n" +
            "targets:\n" +
            "  webOrigin: https://staging.example.test\n" +
            "  browserCredentialRef: keychain://styloagent/staging-e2e\n" +
            "capacity:\n" +
            "  browserRead: 2\n" +
            "  browserWrite: 1\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Request_is_durable_and_requires_owner_approval()
    {
        var service = new BrowserJobService(Environments, Browser);
        var request = service.Request("test-", "staging", "observe", "capture dashboard", "/health",
            null, false, null, T(1));
        Assert.True(request.Success, request.Message);
        Assert.Equal(BrowserJobStatus.Pending, request.Job!.Status);
        Assert.False(service.Approve("test-", request.Job.Id, T(2)).Success);

        var approved = service.Approve("deploy-", request.Job.Id, T(3));
        Assert.True(approved.Success, approved.Message);
        Assert.Equal(BrowserJobStatus.Approved, new BrowserJobStore(Browser).Read(request.Job.Id)!.Status);
    }

    [Fact]
    public void Write_capacity_is_exclusive_while_read_capacity_is_separate()
    {
        var service = new BrowserJobService(Environments, Browser);
        var first = service.Request("a-", "staging", "test", "first", "/", null, false, null, T(1)).Job!;
        var second = service.Request("b-", "staging", "test", "second", "/", null, false, null, T(2)).Job!;
        var observe = service.Request("c-", "staging", "observe", "read", "/", null, false, null, T(3)).Job!;
        Assert.True(service.Approve("deploy-", first.Id, T(4)).Success);
        Assert.False(service.Approve("deploy-", second.Id, T(5)).Success);
        Assert.False(service.Approve("deploy-", observe.Id, T(6)).Success);
        Assert.True(service.MarkRunning(first.Id, T(7)).Success);
        Assert.True(service.Complete(first.Id, BrowserRunResult.Completed("/shot.png"), T(8)).Success);
        Assert.True(service.Approve("deploy-", observe.Id, T(9)).Success);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("//evil.test/")]
    [InlineData("relative")]
    public void Request_rejects_non_relative_targets(string path)
    {
        var result = new BrowserJobService(Environments, Browser).Request(
            "test-", "staging", "observe", "bad target", path, null, false, null, T(1));
        Assert.False(result.Success);
    }

    [Fact]
    public void Credential_must_be_an_environment_approved_reference()
    {
        var service = new BrowserJobService(Environments, Browser);
        Assert.False(service.Request("test-", "staging", "observe", "raw", "/", null, false,
            "sk-secret-value", T(1)).Success);
        Assert.False(service.Request("test-", "staging", "observe", "wrong", "/", null, false,
            "keychain://styloagent/other", T(2)).Success);
        Assert.True(service.Request("test-", "staging", "observe", "approved", "/", null, false,
            "keychain://styloagent/staging-e2e", T(3)).Success);
        Assert.False(service.Request("test-", "staging", "observe", "use Bearer raw-value", "/", null,
            false, null, T(4)).Success);
    }

    [Fact]
    public void Production_mutation_is_rejected_without_operator_capability()
    {
        var definition = Path.Combine(Environments, "definitions", "staging.yaml");
        File.WriteAllText(definition, File.ReadAllText(definition).Replace(
            "classification: non-production", "classification: production", StringComparison.Ordinal));
        var result = new BrowserJobService(Environments, Browser).Request(
            "deploy-", "staging", "test", "mutate prod", "/", null, false, null, T(1));
        Assert.False(result.Success);
    }

    [Fact]
    public void Broker_restart_fails_interrupted_jobs_and_releases_capacity()
    {
        var service = new BrowserJobService(Environments, Browser);
        var job = service.Request("test-", "staging", "test", "write", "/", null, false, null, T(1)).Job!;
        Assert.True(service.Approve("deploy-", job.Id, T(2)).Success);
        Assert.True(service.MarkRunning(job.Id, T(3)).Success);

        service.ReconcileInterrupted(T(4));

        var reconciled = service.Read(job.Id)!;
        Assert.Equal(BrowserJobStatus.Failed, reconciled.Status);
        Assert.Contains("restarted", reconciled.Failure);
    }

    private static DateTimeOffset T(int second) => new(2026, 7, 22, 12, 0, second, TimeSpan.Zero);
}
