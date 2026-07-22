using System.Net;
using System.Net.Sockets;
using System.Text;
using Styloagent.App.Browser;
using Styloagent.Core.Browser;
using Xunit;

namespace Styloagent.App.Tests;

public sealed class PlaywrightBrowserRunnerTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "playwright-runner-" + Guid.NewGuid().ToString("N"));
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    [Fact]
    public async Task Observe_run_captures_a_sanitized_same_origin_screenshot()
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serverCts = new CancellationTokenSource();
        _serverTask = ServeAsync(_serverCts.Token);

        var environments = Path.Combine(_root, "environments");
        var browser = Path.Combine(_root, "browser");
        Directory.CreateDirectory(Path.Combine(environments, "definitions"));
        File.WriteAllText(Path.Combine(environments, "policy.yaml"), "controlOwner: overview-\n");
        File.WriteAllText(Path.Combine(environments, "definitions", "local.yaml"),
            $"id: local\ndisplayName: Local\nowner: overview-\ntargets:\n  webOrigin: http://127.0.0.1:{port}\n");
        var now = DateTimeOffset.UtcNow;
        var job = new BrowserJob("local-run", "test-", "local", BrowserRunMode.Observe, "capture",
            "/", null, false, null, BrowserJobStatus.Running, "overview-", null, null, now, now);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new PlaywrightBrowserRunner(environments, browser).RunAsync(job, timeout.Token);

        Assert.True(result.Success, result.Failure);
        Assert.NotNull(result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath));
        var manifest = await File.ReadAllTextAsync(Path.Combine(browser, "artifacts", job.Id, "manifest.json"));
        Assert.Contains("\"CredentialUsed\": false", manifest);
        Assert.DoesNotContain("<body>", manifest, StringComparison.OrdinalIgnoreCase);
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
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
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
