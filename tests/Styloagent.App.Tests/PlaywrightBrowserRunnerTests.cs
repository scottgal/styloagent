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

    private static BrowserJob Job(string id, string? credentialRef, DateTimeOffset now) => new(
        id, "test-", "local", BrowserRunMode.Observe, "capture", "/", null, false, credentialRef,
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
                    while (await reader.ReadLineAsync(ct) is { } headerLine)
                    {
                        if (headerLine.Length == 0) break;
                        if (headerLine.StartsWith("X-SB-Api-Key:", StringComparison.OrdinalIgnoreCase))
                            _seenApiKey = headerLine[(headerLine.IndexOf(':') + 1)..].Trim();
                    }
                    const string body = "<!doctype html><html><body><h1>Safe page</h1><input type=password value=hidden></body></html>";
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
