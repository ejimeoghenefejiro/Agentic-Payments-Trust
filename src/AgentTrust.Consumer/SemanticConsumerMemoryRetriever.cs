namespace AgentTrust.Consumer;

public sealed class SemanticConsumerMemoryRetriever:IConsumerMemoryRetriever
{
    private readonly IConsumerMemoryStore _store;private readonly IConsumerMemoryEmbeddingService _embeddings;private readonly IConsumerMemoryVectorIndex _vectors;private readonly IConsumerMemoryCache _cache;private readonly TimeSpan _ttl;private readonly Func<DateTimeOffset> _clock;
    public SemanticConsumerMemoryRetriever(IConsumerMemoryStore store,IConsumerMemoryEmbeddingService embeddings,IConsumerMemoryVectorIndex vectors,IConsumerMemoryCache cache,TimeSpan? ttl=null,Func<DateTimeOffset>? clock=null)
    {_store=store;_embeddings=embeddings;_vectors=vectors;_cache=cache;_ttl=ttl??TimeSpan.FromMinutes(2);_clock=clock??(()=>DateTimeOffset.UtcNow);}
    public async ValueTask<IReadOnlyList<ConsumerMemoryMatch>> RetrieveAsync(string principal,string query,int maximum,CancellationToken token=default)
    {
        if(string.IsNullOrWhiteSpace(principal)||string.IsNullOrWhiteSpace(query))return [];
        var hits=await _cache.GetAsync(principal,query,token);
        if(hits is null){var vector=await _embeddings.EmbedAsync(query,token);hits=await _vectors.SearchAsync(principal,vector,maximum,token);await _cache.SetAsync(principal,query,hits,_ttl,token);}
        var now=_clock();var result=new List<ConsumerMemoryMatch>();
        foreach(var hit in hits.Take(maximum)){var item=_store.FindOwned(hit.MemoryId,principal);if(item is not null&&!item.Deleted&&(item.ExpiresAt is null||item.ExpiresAt>now))result.Add(new(item,hit.Score));}
        return result;
    }
}

public sealed class InMemoryConsumerMemoryCache:IConsumerMemoryCache
{
    private readonly Dictionary<string,(DateTimeOffset Expires,IReadOnlyList<ConsumerMemoryVectorHit> Hits)> _items=[];
    public ValueTask<IReadOnlyList<ConsumerMemoryVectorHit>?> GetAsync(string principal,string query,CancellationToken token=default){var key=Key(principal,query);return ValueTask.FromResult(_items.TryGetValue(key,out var x)&&x.Expires>DateTimeOffset.UtcNow?x.Hits:null);}
    public ValueTask SetAsync(string principal,string query,IReadOnlyList<ConsumerMemoryVectorHit> hits,TimeSpan ttl,CancellationToken token=default){_items[Key(principal,query)]=(DateTimeOffset.UtcNow.Add(ttl),hits);return ValueTask.CompletedTask;}
    public ValueTask InvalidatePrincipalAsync(string principal,CancellationToken token=default){foreach(var key in _items.Keys.Where(x=>x.StartsWith($"consumer-memory:{principal}:",StringComparison.Ordinal)).ToList())_items.Remove(key);return ValueTask.CompletedTask;}
    private static string Key(string principal,string query)=>$"consumer-memory:{principal}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(query))).ToLowerInvariant()}";
}
