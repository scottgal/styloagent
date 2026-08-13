using Styloagent.BrowserBroker;
using Styloagent.Core.Environments;
using Xunit;

namespace Styloagent.Core.Tests;

/// <summary>
/// The centralized broker's cross-client pieces: portable artifact references (one client hands a
/// screenshot to another), the env-based header/API-key rules, and the controller surfacing a reference.
/// </summary>
public class BrowserBrokerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "broker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── BrokerArtifacts: the cross-client handoff primitive ─────────────────────────────

    [Fact]
    public void Artifact_reference_round_trips_and_resolves_on_another_client()
    {
        var browserRoot = Path.Combine(_root, "browser");
        var artifact = Path.Combine(browserRoot, "artifacts", "run-1", "screenshot.png");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "png");

        var reference = BrokerArtifacts.Reference(artifact, browserRoot);
        Assert.Equal("broker://run-1/screenshot.png", reference);

        // Client B resolves the SAME reference against ITS OWN root (shared artifacts root) — this is
        // the "one client sends a screenshot to another" primitive.
        var otherRoot = Path.Combine(_root, "other-browser");
        Directory.CreateDirectory(Path.Combine(otherRoot, "artifacts", "run-1"));
        File.Copy(artifact, Path.Combine(otherRoot, "artifacts", "run-1", "screenshot.png"));

        Assert.Equal(Path.Combine(otherRoot, "artifacts", "run-1", "screenshot.png"),
            BrokerArtifacts.Resolve(reference, otherRoot));
    }

    [Fact]
    public void Artifact_reference_never_escapes_the_artifacts_root()
    {
        var browserRoot = Path.Combine(_root, "browser");
        Directory.CreateDirectory(browserRoot);
        Assert.Null(BrokerArtifacts.Resolve("broker://../evil/passwd", browserRoot));
        Assert.Null(BrokerArtifacts.Resolve("broker://run/../../passwd", browserRoot));
        Assert.Null(BrokerArtifacts.Resolve("broker://run/screenshot.png", browserRoot));   // missing file
        var outside = Path.GetTempFileName();
        Assert.Null(BrokerArtifacts.Resolve(outside, browserRoot));                          // raw path outside root
        Assert.True(BrokerArtifacts.IsReference("broker://run/screenshot.png"));
        Assert.False(BrokerArtifacts.IsReference("/tmp/x.png"));
    }

    [Fact]
    public void Reference_returns_null_for_artifacts_outside_the_browser_root()
    {
        var browserRoot = Path.Combine(_root, "browser");
        Directory.CreateDirectory(browserRoot);
        Assert.Null(BrokerArtifacts.Reference("/tmp/unrelated.png", browserRoot));
    }

    // ── EnvironmentBrowserCredentialProvider: centralized headers / API keys ────────────

    [Fact]
    public void Credential_provider_resolves_env_backed_headers()
    {
        Environment.SetEnvironmentVariable("STYLOBOT_TEST_API_KEY", "super-secret");
        try
        {
            var provider = new EnvironmentBrowserCredentialProvider();
            var headers = provider.ResolveHeadersAsync("X-Api-Key=env:STYLOBOT_TEST_API_KEY", default).GetAwaiter().GetResult();
            Assert.Equal("super-secret", headers["X-Api-Key"]);
        }
        finally { Environment.SetEnvironmentVariable("STYLOBOT_TEST_API_KEY", null); }
    }

    [Fact]
    public void Credential_provider_skips_missing_entries_but_fails_closed_when_none_resolve()
    {
        var provider = new EnvironmentBrowserCredentialProvider();
        // Every entry is missing or malformed, so nothing resolves — the provider must throw so a
        // credentialed run never proceeds keyless.
        Assert.Throws<InvalidOperationException>(() => provider.ResolveHeadersAsync(
            "X-Api-Key=env:STYLOBOT_DOES_NOT_EXIST,Authorization=Bearer inline,Keep-Alive=env:", default)
            .GetAwaiter().GetResult());
    }

    [Fact]
    public void Credential_provider_resolves_keychain_entries_through_the_injected_reader()
    {
        string? readItem = null;
        var provider = new EnvironmentBrowserCredentialProvider(item =>
        {
            readItem = item;
            return "keychain-value";
        });
        var headers = provider.ResolveHeadersAsync(
            "X-Api-Key=keychain://styloagent/staging-e2e", default).GetAwaiter().GetResult();
        Assert.Equal("keychain-value", headers["X-Api-Key"]);
        Assert.Equal("styloagent/staging-e2e", readItem);
    }

    [Fact]
    public void Credential_provider_resolves_secret_entries_from_the_process_environment()
    {
        Environment.SetEnvironmentVariable("STYLOBOT_TEST_SECRET_VAR", "secret-value");
        try
        {
            var provider = new EnvironmentBrowserCredentialProvider();
            var headers = provider.ResolveHeadersAsync(
                "X-Api-Key=secret://STYLOBOT_TEST_SECRET_VAR", default).GetAwaiter().GetResult();
            Assert.Equal("secret-value", headers["X-Api-Key"]);
        }
        finally { Environment.SetEnvironmentVariable("STYLOBOT_TEST_SECRET_VAR", null); }
    }

    [Fact]
    public void Credential_provider_mixed_lists_resolve_present_sources_and_skip_missing()
    {
        Environment.SetEnvironmentVariable("STYLOBOT_TEST_MIXED_VAR", "mixed-value");
        try
        {
            var provider = new EnvironmentBrowserCredentialProvider(_ => "keychain-value");
            var headers = provider.ResolveHeadersAsync(
                "X-Api-Key=keychain://staging, X-Trace=env:STYLOBOT_TEST_MIXED_VAR, X-Meta=secret://STYLOBOT_TEST_DOES_NOT_EXIST, X-Bogus=bogus",
                default).GetAwaiter().GetResult();
            Assert.Equal(2, headers.Count);
            Assert.Equal("keychain-value", headers["X-Api-Key"]);
            Assert.Equal("mixed-value", headers["X-Trace"]);
        }
        finally { Environment.SetEnvironmentVariable("STYLOBOT_TEST_MIXED_VAR", null); }
    }

    [Fact]
    public void Credential_provider_resolves_single_values_from_bare_sources()
    {
        Environment.SetEnvironmentVariable("STYLOBOT_TEST_SINGLE_VAR", "single-value");
        try
        {
            var provider = new EnvironmentBrowserCredentialProvider(item => item == "staging-email" ? "e2e@test.dev" : null);
            Assert.Equal("e2e@test.dev", provider.ResolveValue("keychain://staging-email"));
            Assert.Equal("single-value", provider.ResolveValue("env:STYLOBOT_TEST_SINGLE_VAR"));
            Assert.Equal("single-value", provider.ResolveValue("secret://STYLOBOT_TEST_SINGLE_VAR"));
            Assert.Null(provider.ResolveValue("env:STYLOBOT_TEST_DOES_NOT_EXIST"));
            Assert.Null(provider.ResolveValue("keychain://missing-item"));
            Assert.Null(provider.ResolveValue("literal-value"));           // not a source spec
            Assert.Null(provider.ResolveValue("keychain://"));             // empty item
            Assert.Null(provider.ResolveValue(""));                        // empty spec
        }
        finally { Environment.SetEnvironmentVariable("STYLOBOT_TEST_SINGLE_VAR", null); }
    }

    [Fact]
    public void Credential_provider_treats_an_empty_keychain_password_as_unresolved()
    {
        var provider = new EnvironmentBrowserCredentialProvider(_ => "");
        Assert.Throws<InvalidOperationException>(() => provider.ResolveHeadersAsync(
            "X-Api-Key=keychain://staging-empty", default).GetAwaiter().GetResult());
    }

    [Fact]
    public void Credential_provider_null_or_empty_reference_returns_no_headers_without_throwing()
    {
        var provider = new EnvironmentBrowserCredentialProvider(_ => null);
        Assert.Empty(provider.ResolveHeadersAsync(null!, default).GetAwaiter().GetResult());
        Assert.Empty(provider.ResolveHeadersAsync("", default).GetAwaiter().GetResult());
        Assert.Empty(provider.ResolveHeadersAsync("   ", default).GetAwaiter().GetResult());
    }

    [Fact]
    public void Credential_provider_header_names_are_case_insensitive_and_last_entry_wins()
    {
        Environment.SetEnvironmentVariable("STYLOBOT_TEST_CASE_VAR", "case-value");
        try
        {
            var provider = new EnvironmentBrowserCredentialProvider();
            var headers = provider.ResolveHeadersAsync(
                "X-Api-Key=env:STYLOBOT_TEST_CASE_VAR, x-api-key=env:STYLOBOT_TEST_CASE_VAR", default)
                .GetAwaiter().GetResult();
            Assert.Single(headers);
            Assert.Equal("case-value", headers["X-Api-Key"]);
        }
        finally { Environment.SetEnvironmentVariable("STYLOBOT_TEST_CASE_VAR", null); }
    }

    // ── Controller surfaces a broker:// reference after a completed run ─────────────────

    [Fact]
    public async Task Controller_artifacts_returns_a_portable_reference()
    {
        var environmentsRoot = Path.Combine(_root, "environments");
        var browserRoot = Path.Combine(_root, "browser");
        EnvironmentRegistry.Create(environmentsRoot, "staging", "Staging", "non-production", "deploy-");
        Assert.True(EnvironmentRegistry.ConfigureBrowser(environmentsRoot, "staging",
            "https://staging.example.com", null, readCapacity: 4, writeCapacity: 1).Success);
        // The test caller acts as the environment control owner (approval authority).
        File.WriteAllText(EnvironmentRegistry.PolicyFile(environmentsRoot), "controlOwner: test-\n");
        var host = new TestHost(environmentsRoot, browserRoot);
        var controller = new BrowserController(host);

        var service = new BrowserJobService(environmentsRoot, browserRoot);
        var created = service.Request("test-", "staging", BrowserRunMode.Observe.ToString().ToLowerInvariant(),
            "capture", "/", null, false, null, DateTimeOffset.UtcNow);
        Assert.True(created.Success);
        var id = created.Job!.Id;

        // Drive the lifecycle without launching Playwright: approve → mark running → complete with an
        // artifact written under the browser root.
        var artifact = Path.Combine(browserRoot, "artifacts", id, "screenshot.png");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "png");
        Assert.True(service.Approve("test-", id, DateTimeOffset.UtcNow).Success);   // control owner approves
        Assert.True(service.MarkRunning(id, DateTimeOffset.UtcNow).Success);
        Assert.True(service.Complete(id, BrowserRunResult.Completed(artifact), DateTimeOffset.UtcNow).Success);

        var result = await controller.ArtifactsAsync("test-", id).ConfigureAwait(false);
        Assert.Equal($"broker://{id}/screenshot.png", result);

        // And it resolves on a SECOND client sharing the artifacts root.
        var otherRoot = Path.Combine(_root, "other-browser");
        Directory.CreateDirectory(Path.Combine(otherRoot, "artifacts", id));
        File.Copy(artifact, Path.Combine(otherRoot, "artifacts", id, "screenshot.png"));
        Assert.Equal(Path.Combine(otherRoot, "artifacts", id, "screenshot.png"),
            BrokerArtifacts.Resolve(result, otherRoot));
    }

    private sealed class TestHost(string environments, string browser) : IBrowserControllerHost
    {
        public string? EnvironmentsRoot { get; } = environments;
        public string? BrowserRoot { get; } = browser;
        public void NotifyBrowserRefresh() { }
    }
}
