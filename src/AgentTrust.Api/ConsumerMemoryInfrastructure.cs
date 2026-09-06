using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentTrust.Consumer;
using AgentTrust.Intelligence.Investigation;
using Microsoft.Extensions.Caching.Distributed;

namespace AgentTrust.Api;

public sealed class ConsumerEmbeddingAdapter:IConsumerMemoryEmbeddingService
{
    private readonly ITextEmbeddingService _inner;public ConsumerEmbeddingAdapter(ITextEmbeddingService inner)=>_inner=inner;
    public int Dimensions=>_inner.Dimensions;
    public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text,CancellationToken token=default)=>_inner.EmbedAsync(text,token);
}

public sealed class RedisConsumerMemoryCache: IConsumerMemoryCache
{
    private readonly IDistributedCache _cache;public RedisConsumerMemoryCache(IDistributedCache cache)=>_cache=cache;
    public async ValueTask<IReadOnlyList<ConsumerMemoryVectorHit>?> GetAsync(string principal,string query,CancellationToken token=default)
    {var json=await _cache.GetStringAsync(await Key(principal,query,token),token);return json is null?null:JsonSerializer.Deserialize<ConsumerMemoryVectorHit[]>(json);}
    public async ValueTask SetAsync(string principal,string query,IReadOnlyList<ConsumerMemoryVectorHit> hits,TimeSpan ttl,CancellationToken token=default)
    {await _cache.SetStringAsync(await Key(principal,query,token),JsonSerializer.Serialize(hits),new DistributedCacheEntryOptions{AbsoluteExpirationRelativeToNow=ttl},token);}
    public ValueTask InvalidatePrincipalAsync(string principal,CancellationToken token=default)=>new(_cache.SetStringAsync(GenerationKey(principal),Guid.NewGuid().ToString("N"),new DistributedCacheEntryOptions(),token));
    private async Task<string> Key(string principal,string query,CancellationToken token){var generation=await _cache.GetStringAsync(GenerationKey(principal),token)??"0";var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();return $"consumer-memory:{principal}:{generation}:{hash}";}
    private static string GenerationKey(string principal)=>$"consumer-memory-generation:{principal}";
}

public sealed class QdrantConsumerMemoryVectorIndex:IConsumerMemoryVectorIndex
{
    private readonly HttpClient _http;private readonly IConsumerMemoryEmbeddingService _embeddings;private readonly string _collection;private readonly SemaphoreSlim _initialization=new(1,1);private bool _ready;
    public QdrantConsumerMemoryVectorIndex(HttpClient http,IConsumerMemoryEmbeddingService embeddings,IConfiguration configuration){_http=http;_embeddings=embeddings;_collection=configuration["ConsumerMemory:Qdrant:Collection"]??"consumer_memories";}
    public async ValueTask UpsertAsync(ConsumerMemoryEntry memory,ReadOnlyMemory<float> embedding,CancellationToken token=default)
    {
        await EnsureCollection(token);var body=new{points=new[]{new{id=PointId(memory.MemoryId),vector=embedding.ToArray(),payload=new{memory_id=memory.MemoryId,principal_id=memory.PrincipalId,kind=memory.Kind.ToString(),polarity=memory.Polarity.ToString(),provenance=memory.Provenance,expires_at=memory.ExpiresAt?.ToUnixTimeSeconds()}}}};
        using var response=await _http.PutAsJsonAsync($"collections/{_collection}/points?wait=true",body,token);response.EnsureSuccessStatusCode();
    }
    public async ValueTask DeleteAsync(string principal,string memoryId,CancellationToken token=default)
    {
        await EnsureCollection(token);var body=new{filter=new{must=new object[]{Match("principal_id",principal),Match("memory_id",memoryId)}}};using var response=await _http.PostAsJsonAsync($"collections/{_collection}/points/delete?wait=true",body,token);response.EnsureSuccessStatusCode();
    }
    public async ValueTask<IReadOnlyList<ConsumerMemoryVectorHit>> SearchAsync(string principal,ReadOnlyMemory<float> vector,int maximum,CancellationToken token=default)
    {
        await EnsureCollection(token);var body=new{query=vector.ToArray(),filter=new{must=new[]{Match("principal_id",principal)}},limit=Math.Clamp(maximum,1,50),with_payload=true};using var response=await _http.PostAsJsonAsync($"collections/{_collection}/points/query",body,token);response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        if(!json.RootElement.TryGetProperty("result",out var result)||!result.TryGetProperty("points",out var points))return [];
        return points.EnumerateArray().Select(x=>new ConsumerMemoryVectorHit(x.GetProperty("payload").GetProperty("memory_id").GetString()!,x.GetProperty("score").GetDouble())).ToList();
    }
    private async Task EnsureCollection(CancellationToken token){if(_ready)return;await _initialization.WaitAsync(token);try{if(_ready)return;using var existing=await _http.GetAsync($"collections/{_collection}",token);if(existing.StatusCode==System.Net.HttpStatusCode.NotFound){using var created=await _http.PutAsJsonAsync($"collections/{_collection}",new{vectors=new{size=_embeddings.Dimensions,distance="Cosine"}},token);created.EnsureSuccessStatusCode();}else existing.EnsureSuccessStatusCode();_ready=true;}finally{_initialization.Release();}}
    private static object Match(string key,string value)=>new{key,match=new{value}};
    private static string PointId(string memoryId)=>new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(memoryId))[..16]).ToString();
}

public sealed class ConsumerMemoryIndexWorker:BackgroundService
{
    private readonly IServiceScopeFactory _scopes;private readonly ILogger<ConsumerMemoryIndexWorker> _logger;private readonly TimeSpan _interval;
    public ConsumerMemoryIndexWorker(IServiceScopeFactory scopes,IConfiguration configuration,ILogger<ConsumerMemoryIndexWorker> logger){_scopes=scopes;_logger=logger;_interval=TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("ConsumerMemory:OutboxIntervalSeconds",5),1,300));}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested){await Process(stoppingToken);await Task.Delay(_interval,stoppingToken);}
    }
    internal async Task Process(CancellationToken token)
    {
        using var scope=_scopes.CreateScope();var outbox=scope.ServiceProvider.GetRequiredService<IConsumerMemoryOutboxStore>();var vectors=scope.ServiceProvider.GetRequiredService<IConsumerMemoryVectorIndex>();var embeddings=scope.ServiceProvider.GetRequiredService<IConsumerMemoryEmbeddingService>();var cache=scope.ServiceProvider.GetRequiredService<IConsumerMemoryCache>();
        foreach(var item in outbox.Pending(50,DateTimeOffset.UtcNow)){try{var memory=outbox.FindMemory(item.MemoryId);if(item.Operation=="Delete"||memory is null||memory.Deleted)await vectors.DeleteAsync(item.PrincipalId,item.MemoryId,token);else{var vector=await embeddings.EmbedAsync($"{memory.Kind} {memory.Polarity} {memory.Subject} {memory.Content}",token);await vectors.UpsertAsync(memory,vector,token);}await cache.InvalidatePrincipalAsync(item.PrincipalId,token);outbox.Complete(item.OutboxId,DateTimeOffset.UtcNow);}catch(Exception ex){_logger.LogWarning(ex,"Consumer memory outbox {OutboxId} failed",item.OutboxId);outbox.Fail(item.OutboxId,ex.Message,DateTimeOffset.UtcNow);}}
    }
}
