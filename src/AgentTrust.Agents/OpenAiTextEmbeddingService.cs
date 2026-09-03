using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgentTrust.Intelligence.Investigation;

namespace AgentTrust.Agents;

public sealed class OpenAiTextEmbeddingService : ITextEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public OpenAiTextEmbeddingService(HttpClient httpClient, string apiKey, string model, int dimensions, string? modelVersion = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        _httpClient = httpClient;
        _apiKey = apiKey;
        Model = model;
        ModelVersion = modelVersion;
        Dimensions = dimensions;
    }

    public string Provider => "OpenAI";
    public string Model { get; }
    public string? ModelVersion { get; }
    public int Dimensions { get; }

    public async ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = JsonContent.Create(new EmbeddingRequest(Model, text, Dimensions));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Embedding provider returned an empty response.");
        var vector = payload.Data?.OrderBy(x => x.Index).FirstOrDefault()?.Embedding;
        if (vector is null || vector.Count == 0) throw new InvalidOperationException("Embedding provider returned no vector.");
        if (vector.Count != Dimensions) throw new InvalidOperationException($"Embedding provider returned {vector.Count} dimensions; expected {Dimensions}.");
        if (vector.Any(value => !float.IsFinite(value))) throw new InvalidOperationException("Embedding provider returned a non-finite vector.");
        return vector.ToArray();
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("dimensions")] int Dimensions);
    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] IReadOnlyList<EmbeddingData>? Data);
    private sealed record EmbeddingData(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] IReadOnlyList<float> Embedding);
}
