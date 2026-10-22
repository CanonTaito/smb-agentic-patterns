using System.Net;
using System.Text;
using Agentic.AgentCore;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class OllamaClientEmbedTests
{
    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task EmbedAsync_PostsToApiEmbed_AndParsesFirstVector()
    {
        var handler = new StubHandler("""{"model":"nomic-embed-text","embeddings":[[0.1,0.2,0.3]]}""");
        var client = new OllamaClient(http: new HttpClient(handler));

        var vector = await client.EmbedAsync("hello", "nomic-embed-text");

        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, vector);
        Assert.NotNull(handler.Seen);
        Assert.Equal("/api/embed", handler.Seen!.RequestUri!.AbsolutePath);
        var body = await handler.Seen.Content!.ReadAsStringAsync();
        Assert.Contains("nomic-embed-text", body);
        Assert.Contains("hello", body);
    }

    [Fact]
    public async Task EmbedAsync_LegacyEmptyEmbedding_ThrowsInsteadOfReturningZeroVector()
    {
        // Ollama 0.40's legacy /api/embeddings replies {"embedding":[]};
        // posting to /api/embed must never let an empty payload through as a
        // silent zero-length vector (it made every cosine score 0.0).
        var handler = new StubHandler("""{"embeddings":[]}""");
        var client = new OllamaClient(http: new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync("hello"));
    }
}
