using System.Net;
using System.Net.Sockets;
using System.Text;
using Styloagent.Core.Memory;
using Styloagent.Core.Retrieval;

namespace Styloagent.Core.Tests;

public sealed class DocumentQuestionServiceTests
{
    [Fact]
    public async Task AnswerAsync_uses_the_configured_local_model_for_grounded_synthesis()
    {
        var root = Path.Combine(Path.GetTempPath(), "styloagent-doc-question-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "bus.md"), "# Signal bus\n\nThe bus archives a task after its recipient replies.");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestSeen = ReceiveGenerateRequestAsync(listener);
        try
        {
            var options = new MemoryRagOptions("memory", "index.json", $"http://127.0.0.1:{port}", "embed", SynthesisModel: "test-local-model");
            var answer = await DocumentQuestionService.AnswerAsync(root, options, "When is a task archived?");

            var request = await requestSeen;
            Assert.Contains("/api/generate", request);
            Assert.True(answer.Synthesized);
            Assert.Contains("after a reply", answer.Markdown);
            Assert.Single(answer.Sources);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ReceiveGenerateRequestAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var request = new StringBuilder();
        string? line;
        int contentLength = 0;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            request.AppendLine(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
        }
        var body = new char[contentLength];
        var offset = 0;
        while (offset < body.Length)
            offset += await reader.ReadAsync(body.AsMemory(offset));
        request.Append(body);

        var response = "{\"response\":\"A task is archived after a reply. [S1]\"}";
        var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(response)}\r\nConnection: close\r\n\r\n{response}");
        await stream.WriteAsync(bytes);
        return request.ToString();
    }
}
