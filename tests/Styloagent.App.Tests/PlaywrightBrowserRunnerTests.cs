using System.Net;
using System.Net.Sockets;
using System.Text;
using Styloagent.BrowserBroker;
using Styloagent.BrowserBroker;
using Xunit;

namespace Styloagent.App.Tests;

public sealed class PlaywrightBrowserRunnerTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "playwright-runner-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private volatile string? _seenApiKey;

    private void StartServer()
    {
        _listener.Start();
        _serverCts = new CancellationTokenSource();
        _serverTask = ServeAsync(_serverCts.Token);
    }

    private string LocalOrigin => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    private void WriteEnvironment(string? credentialRef = null)
    {
        var environments = Path.Combine(_root, "environments");
        var browser = Path.Combine(_root, "browser");
        Directory.CreateDirectory(Path.Combine(environments, "definitions"));
        File.WriteAllText(Path.Combine(environments, "policy.yaml"), "controlOwner: overview-\n");
        var refLine = credentialRef is null ? "" : $"  browserCredentialRef: {credentialRef}\n";
        File.WriteAllText(Path.Combine(environments, "definitions", "local.yaml"),
            $"id: local\ndisplayName: Local\nowner: overview-\ntargets:\n  webOrigin: {LocalOrigin}\n{refLine}");
    }

    private static BrowserJob Job(string id, string? credentialRef, DateTimeOffset now,
        LoginStep? login = null, BrowserRunMode mode = BrowserRunMode.Observe) => new(
        id, "test-", "local", mode, "capture", "/", null, false, credentialRef, login,
        BrowserJobStatus.Running, "overview-", null, null, now, now);

    [Fact]
    public async Task Observe_run_captures_a_sanitized_same_origin_screenshot()
    {
        StartServer();
        WriteEnvironment();
        var browser = Path.Combine(_root, "browser");
        var now = DateTimeOffset.UtcNow;
        var job = Job("local-run", null, now);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new PlaywrightBrowserRunner(Path.Combine(_root, "environments"), browser)
            .RunAsync(job, timeout.Token);

        Assert.True(result.Success, result.Failure);
        Assert.NotNull(result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath));
        var manifest = await File.ReadAllTextAsync(Path.Combine(browser, "artifacts", job.Id, "manifest.json"));
        Assert.Contains("\"CredentialUsed\": false", manifest);
        Assert.DoesNotContain("<body>", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolved_keychain_reference_attaches_the_header_to_requests()
    {
        StartServer();
        WriteEnvironment("X-SB-Api-Key=keychain://staging-debug-key, X-Trace=env:UNSET_TRACE_VAR");
        var browser = Path.Combine(_root, "browser");
        var job = Job("keychain-run", "X-SB-Api-Key=keychain://staging-debug-key, X-Trace=env:UNSET_TRACE_VAR",
            DateTimeOffset.UtcNow);
        var runner = new PlaywrightBrowserRunner(Path.Combine(_root, "environments"), browser,
            new EnvironmentBrowserCredentialProvider(_ => "test-key-value-123"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await runner.RunAsync(job, timeout.Token);

        Assert.True(result.Success, result.Failure);
        Assert.Equal("test-key-value-123", _seenApiKey);
    }

    [Fact]
    public async Task Unresolvable_credential_reference_fails_the_run_closed()
    {
        StartServer();
        WriteEnvironment("X-SB-Api-Key=env:STYLOAGENT_TEST_UNSET_RUNNER_VAR");
        var browser = Path.Combine(_root, "browser");
        var job = Job("keyless-run", "X-SB-Api-Key=env:STYLOAGENT_TEST_UNSET_RUNNER_VAR",
            DateTimeOffset.UtcNow);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new PlaywrightBrowserRunner(Path.Combine(_root, "environments"), browser)
            .RunAsync(job, timeout.Token);

        Assert.False(result.Success);
        Assert.Equal("approved credential reference could not be resolved", result.Failure);
        Assert.Null(_seenApiKey);
    }

    [Fact]
    public async Task Login_step_fills_and_submits_then_screenshots_the_post_login_page()
    {
        StartServer();
        _getBody = LoginForm;
        _postBody = PostLoginPage;
        WriteEnvironment();
        var job = Job("login-run", null, DateTimeOffset.UtcNow,
            new LoginStep("keychain://fixture-email", "keychain://fixture-password"), BrowserRunMode.Test);
        var runner = new PlaywrightBrowserRunner(Path.Combine(_root, "environments"), Path.Combine(_root, "browser"),
            new EnvironmentBrowserCredentialProvider(item => item switch
            {
                "fixture-email" => "fixture-email@test.dev",
                "fixture-password" => "fixture-secret-456",
                _ => null,
            }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await runner.RunAsync(job, timeout.Token);

        Assert.True(result.Success, result.Failure);
        var manifest = await File.ReadAllTextAsync(Path.Combine(_root, "browser", "artifacts", job.Id, "manifest.json"));
        Assert.Contains("\"LoginUsed\": true", manifest);
        Assert.Contains("\"LoginCompleted\": true", manifest);
        // Secret-never-logged: resolved values never reach the manifest or any artifact file.
        Assert.DoesNotContain("fixture-email@test.dev", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret-456", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_step_falls_back_to_username_and_password_name_selectors()
    {
        StartServer();
        _getBody = FallbackLoginForm;
        _postBody = PostLoginPage;
        WriteEnvironment();
        var job = Job("login-fallback-run", null, DateTimeOffset.UtcNow,
            new LoginStep("env:FIXTURE_EMAIL", "env:FIXTURE_PASSWORD"), BrowserRunMode.Test);
        Environment.SetEnvironmentVariable("FIXTURE_EMAIL", "fallback@test.dev");
        Environment.SetEnvironmentVariable("FIXTURE_PASSWORD", "fallback-secret-789");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await new PlaywrightBrowserRunner(Path.Combine(_root, "environments"),
                Path.Combine(_root, "browser"), new EnvironmentBrowserCredentialProvider())
                .RunAsync(job, timeout.Token);

            Assert.True(result.Success, result.Failure);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FIXTURE_EMAIL", null);
            Environment.SetEnvironmentVariable("FIXTURE_PASSWORD", null);
        }
    }

    [Fact]
    public async Task Login_step_that_never_completes_fails_but_still_screenshots()
    {
        StartServer();
        _getBody = LoginForm;
        _postBody = LoginForm; // wrong-credentials page keeps the password field — never detaches
        WriteEnvironment();
        var job = Job("login-timeout-run", null, DateTimeOffset.UtcNow,
            new LoginStep("keychain://fixture-email", "keychain://fixture-password"), BrowserRunMode.Test);
        var runner = new PlaywrightBrowserRunner(Path.Combine(_root, "environments"), Path.Combine(_root, "browser"),
            new EnvironmentBrowserCredentialProvider(_ => "wrong-credentials-value"), TimeSpan.FromSeconds(1));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await runner.RunAsync(job, timeout.Token);

        Assert.False(result.Success);
        Assert.Equal("login step did not complete", result.Failure);
        Assert.True(File.Exists(Path.Combine(_root, "browser", "artifacts", job.Id, "screenshot.png")));
        var manifest = await File.ReadAllTextAsync(Path.Combine(_root, "browser", "artifacts", job.Id, "manifest.json"));
        Assert.Contains("\"LoginCompleted\": false", manifest);
        Assert.DoesNotContain("wrong-credentials-value", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_step_with_unresolvable_references_fails_before_launching()
    {
        StartServer();
        WriteEnvironment();
        var job = Job("login-unresolved-run", null, DateTimeOffset.UtcNow,
            new LoginStep("env:STYLOAGENT_TEST_UNSET_LOGIN_EMAIL", "env:STYLOAGENT_TEST_UNSET_LOGIN_PASSWORD"),
            BrowserRunMode.Test);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new PlaywrightBrowserRunner(Path.Combine(_root, "environments"),
            Path.Combine(_root, "browser")).RunAsync(job, timeout.Token);

        Assert.False(result.Success);
        Assert.Equal("login step could not resolve its email/password references", result.Failure);
    }

    private const string SafePage = "<!doctype html><html><body><h1>Safe page</h1><input type=password value=hidden></body></html>";
    private const string LoginForm =
        "<!doctype html><html><body><form method=\"post\" action=\"/\">" +
        "<input type=\"email\" name=\"email\"><input type=\"password\" name=\"password\">" +
        "<input type=\"submit\" value=\"Sign in\"></form></body></html>";
    private const string FallbackLoginForm =
        "<!doctype html><html><body><form method=\"post\" action=\"/\">" +
        "<input name=\"username\"><input name=\"password\">" +
        "<button type=\"submit\">Sign in</button></form></body></html>";
    private const string PostLoginPage = "<!doctype html><html><body><h1>Dashboard</h1></body></html>";

    private volatile string _getBody = SafePage;
    private volatile string _postBody = SafePage;

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(ct);
                    var isPost = requestLine?.StartsWith("POST", StringComparison.OrdinalIgnoreCase) == true;
                    while (await reader.ReadLineAsync(ct) is { } headerLine)
                    {
                        if (headerLine.Length == 0) break;
                        if (headerLine.StartsWith("X-SB-Api-Key:", StringComparison.OrdinalIgnoreCase))
                            _seenApiKey = headerLine[(headerLine.IndexOf(':') + 1)..].Trim();
                    }
                    var body = isPost ? _postBody : _getBody;
                    var response = Encoding.UTF8.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                    await stream.WriteAsync(response, ct);
                }
            }, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_serverCts is not null) await _serverCts.CancelAsync();
        _listener.Stop();
        if (_serverTask is not null)
            try { await _serverTask; } catch (OperationCanceledException) { }
        _serverCts?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
