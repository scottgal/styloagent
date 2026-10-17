using System.Net;
using Styloagent.Core.Mcp;
using Styloagent.Core.Sessions;

namespace Styloagent.Core.Tests;

public class DeepSeekModelDiscoveryTests
{
    private const string Catalog = """
        {"data":[
          {"id":"deepseek/deepseek-v4.1-flash-1017","name":"Flash 1017","created":100,"context_length":1000000,"architecture":{"output_modalities":["text"]},"supported_parameters":["tools","reasoning"]},
          {"id":"deepseek/deepseek-v5-flash-0101","name":"Flash 5","created":200,"context_length":2000000,"architecture":{"output_modalities":["text"]},"supported_parameters":["tools","reasoning_effort"]},
          {"id":"deepseek/deepseek-v5-pro","created":300,"architecture":{"output_modalities":["text"]},"supported_parameters":["tools"]},
          {"id":"deepseek/deepseek-v6-flash-preview","created":400,"architecture":{"output_modalities":["text"]},"supported_parameters":["tools"]},
          {"id":"deepseek/embedding","architecture":{"output_modalities":["embedding"]},"supported_parameters":[]},
          {"id":"other/flash","architecture":{"output_modalities":["text"]},"supported_parameters":["tools"]},
          null,{"id":123},{"id":"deepseek/broken","architecture":null,"supported_parameters":["tools"]}
        ]}
        """;

    [Fact]
    public void Catalog_discovers_future_models_but_auto_default_stays_with_stable_flash()
    {
        var models = DeepSeekModelDiscovery.ParseCatalog(Catalog);
        Assert.Equal(4, models.Count);
        Assert.Equal("deepseek/deepseek-v5-flash-0101", DeepSeekModelDiscovery.LatestFlash(models));
        var future = Assert.Single(models, m => m.Capability.Id == "deepseek/deepseek-v5-flash-0101");
        Assert.Equal(2000000, future.ContextLength);
        Assert.Equal("default", Assert.Single(future.Capability.Efforts));
        Assert.Equal("default", Assert.Single(Assert.Single(models, m => m.Capability.Id.EndsWith("-pro")).Capability.Efforts));
        Assert.True(AgentCapabilities.Load(null).WithDeepSeekModels(models.Select(m => m.Capability).ToArray())
            .Supports("claude-deepseek", future.Capability.Id, "default"));
    }

    [Theory]
    [InlineData(null)] [InlineData("not json")] [InlineData("[]")] [InlineData("{\"data\":null}")]
    public void Invalid_catalog_retains_fallback(string? json)
    {
        Assert.Empty(DeepSeekModelDiscovery.ParseCatalog(json));
        Assert.Equal(DeepSeekModels.OpenRouterFlash, DeepSeekModelDiscovery.LatestFlash([]));
    }

    [Fact]
    public void Endpoint_mapping_follows_latest_only_for_aliases_and_preserves_pins()
    {
        const string latest = "deepseek/deepseek-v5-flash-0101";
        Assert.Equal(latest, DeepSeekModels.ResolveForEndpoint("deepseek-flash", "https://openrouter.ai/api", latest));
        Assert.Equal(DeepSeekModels.OpenRouterFlash, DeepSeekModels.ResolveForEndpoint(DeepSeekModels.OpenRouterFlash, "https://openrouter.ai/api", latest));
        Assert.Equal("deepseek-flash", DeepSeekModels.ResolveForEndpoint("deepseek-flash", "https://api.deepseek.com/anthropic", latest));
        Assert.Equal("deepseek-flash", DeepSeekModels.ResolveForEndpoint("deepseek-flash", "https://openrouter.ai.example/api", latest));
    }

    [Fact]
    public async Task Refresh_keeps_last_good_results_after_http_or_schema_failure()
    {
        var handler = new CatalogHandler();
        using var client = new HttpClient(handler);
        var discovery = new DeepSeekModelDiscovery(client);
        await discovery.RefreshAsync("test-token");
        Assert.Equal("Bearer test-token", handler.Authorization);
        Assert.Equal(4, discovery.CachedModels.Count);
        handler.Response = new(HttpStatusCode.ServiceUnavailable);
        await discovery.RefreshAsync("test-token");
        Assert.Equal(4, discovery.CachedModels.Count);
        handler.Response = new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        await discovery.RefreshAsync("test-token");
        Assert.Equal(4, discovery.CachedModels.Count);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response = new(HttpStatusCode.OK) { Content = new StringContent(Catalog) };
        public string? Authorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(Response);
        }
    }
}
