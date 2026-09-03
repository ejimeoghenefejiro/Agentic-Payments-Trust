using System.Net;
using System.Text;
using AgentTrust.Agents;

namespace AgentTrust.Tests.Intelligence;

public sealed class OpenAiTextEmbeddingServiceTests
{
    [Fact]
    public async Task ParsesEmbeddingAndSendsBearerCredentialWithoutPersistingIt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[0.1,0.2,0.3]}]}""");
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://embeddings.example/v1/") };
        var service = new OpenAiTextEmbeddingService(client, "secret-from-runtime", "embedding-model", 3, "2026-01");

        var vector = await service.EmbedAsync("new handset overseas");

        Assert.Equal(new[] { .1f, .2f, .3f }, vector.ToArray());
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-from-runtime", handler.Request.Headers.Authorization.Parameter);
        Assert.Equal("OpenAI", service.Provider);
        Assert.Equal("2026-01", service.ModelVersion);
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[0.1,0.2]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[0.1,\"NaN\",0.3]}]}")]
    public async Task RejectsEmptyWrongDimensionOrNonFiniteResponses(string json)
    {
        var client = new HttpClient(new StubHandler(HttpStatusCode.OK, json)) { BaseAddress = new Uri("https://embeddings.example/v1/") };
        var service = new OpenAiTextEmbeddingService(client, "runtime-secret", "model", 3);
        await Assert.ThrowsAnyAsync<Exception>(async () => await service.EmbedAsync("case text"));
    }

    private sealed class StubHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
