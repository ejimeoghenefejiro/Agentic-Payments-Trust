using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTrust.Consumer;

public enum ConsumerMemoryPolarity { Positive, Negative, Neutral }
public enum ConsumerMemoryKind { Preference, Correction, Inventory, Accessibility, Substitution, Household }

public sealed record ConsumerMemoryEntry(string MemoryId,string PrincipalId,ConsumerMemoryKind Kind,
    ConsumerMemoryPolarity Polarity,string Subject,string Content,string Provenance,string? SourceConversationId,
    string? SourcePurchaseIntentId,double Confidence,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt=null,bool Deleted=false,long Version=1);
public sealed record ConsumerMemoryMatch(ConsumerMemoryEntry Memory,double Score);
public sealed record ConsumerMemoryRetrievalAudit(string AuditId,string PrincipalId,string Query,
    IReadOnlyList<string> ReturnedMemoryIds,DateTimeOffset RetrievedAt);
public sealed record ConsumerMemoryVectorHit(string MemoryId,double Score);
public sealed record ConsumerMemoryOutboxItem(string OutboxId,string MemoryId,string PrincipalId,string Operation,
    string Status,int Attempts,DateTimeOffset CreatedAt,DateTimeOffset? ProcessedAt=null,string? LastError=null);

public interface IConsumerMemoryRetriever
{
    ValueTask<IReadOnlyList<ConsumerMemoryMatch>> RetrieveAsync(string principalId,string query,int maximum,CancellationToken cancellationToken=default);
}
public interface IConsumerMemoryVectorIndex
{
    ValueTask UpsertAsync(ConsumerMemoryEntry memory,ReadOnlyMemory<float> embedding,CancellationToken cancellationToken=default);
    ValueTask DeleteAsync(string principalId,string memoryId,CancellationToken cancellationToken=default);
    ValueTask<IReadOnlyList<ConsumerMemoryVectorHit>> SearchAsync(string principalId,ReadOnlyMemory<float> queryEmbedding,int maximum,CancellationToken cancellationToken=default);
}
public interface IConsumerMemoryEmbeddingService
{
    int Dimensions{get;}
    ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text,CancellationToken cancellationToken=default);
}
public interface IConsumerMemoryCache
{
    ValueTask<IReadOnlyList<ConsumerMemoryVectorHit>?> GetAsync(string principalId,string query,CancellationToken cancellationToken=default);
    ValueTask SetAsync(string principalId,string query,IReadOnlyList<ConsumerMemoryVectorHit> hits,TimeSpan ttl,CancellationToken cancellationToken=default);
    ValueTask InvalidatePrincipalAsync(string principalId,CancellationToken cancellationToken=default);
}
public interface IConsumerMemoryOutboxStore
{
    IReadOnlyList<ConsumerMemoryOutboxItem> Pending(int maximum,DateTimeOffset now);
    ConsumerMemoryEntry? FindMemory(string memoryId);
    void Complete(string outboxId,DateTimeOffset now);
    void Fail(string outboxId,string error,DateTimeOffset now);
}

public interface IConsumerMemoryStore
{
    void Save(ConsumerMemoryEntry memory);
    ConsumerMemoryEntry? FindOwned(string memoryId,string principalId);
    IReadOnlyList<ConsumerMemoryEntry> ActiveForPrincipal(string principalId,DateTimeOffset now);
    void Delete(ConsumerMemoryEntry memory);
    void RecordRetrieval(ConsumerMemoryRetrievalAudit audit);
    IReadOnlyList<ConsumerMemoryRetrievalAudit> Retrievals(string principalId);
}

public interface IConsumerMemoryService
{
    ConsumerMemoryEntry Remember(string principalId,ConsumerMemoryKind kind,ConsumerMemoryPolarity polarity,
        string subject,string content,string provenance,string? conversationId=null,string? purchaseIntentId=null,
        double confidence=1,DateTimeOffset? expiresAt=null);
    IReadOnlyList<ConsumerMemoryMatch> Retrieve(string principalId,string query,int maximum=8);
    ValueTask<IReadOnlyList<ConsumerMemoryMatch>> RetrieveAsync(string principalId,string query,int maximum=8,CancellationToken cancellationToken=default);
    IReadOnlyList<ConsumerMemoryEntry> Export(string principalId);
    bool Delete(string principalId,string memoryId);
    IReadOnlyList<ConsumerMemoryEntry> CaptureCorrections(string principalId,string message,string? conversationId=null);
}

public sealed class ConsumerMemoryService:IConsumerMemoryService
{
    private readonly IConsumerMemoryStore _store;private readonly Func<DateTimeOffset> _clock;private readonly IConsumerMemoryRetriever? _retriever;
    public ConsumerMemoryService(IConsumerMemoryStore store,Func<DateTimeOffset>? clock=null,IConsumerMemoryRetriever? retriever=null){_store=store;_clock=clock??(()=>DateTimeOffset.UtcNow);_retriever=retriever;}
    public ConsumerMemoryEntry Remember(string principalId,ConsumerMemoryKind kind,ConsumerMemoryPolarity polarity,string subject,string content,string provenance,string? conversationId=null,string? purchaseIntentId=null,double confidence=1,DateTimeOffset? expiresAt=null)
    {
        if(string.IsNullOrWhiteSpace(principalId)||string.IsNullOrWhiteSpace(subject)||string.IsNullOrWhiteSpace(content)||string.IsNullOrWhiteSpace(provenance))throw new ArgumentException("Memory owner, subject, content and provenance are required.");
        if(confidence is <0 or >1)throw new ArgumentOutOfRangeException(nameof(confidence));var now=_clock();
        var existing=_store.ActiveForPrincipal(principalId,now).FirstOrDefault(x=>x.Kind==kind&&x.Subject.Equals(subject,StringComparison.OrdinalIgnoreCase));
        var item=existing is null?new($"cmem_{Guid.NewGuid():N}",principalId,kind,polarity,subject.Trim(),content.Trim(),provenance,conversationId,purchaseIntentId,confidence,now,now,expiresAt)
            :existing with{Polarity=polarity,Content=content.Trim(),Provenance=provenance,SourceConversationId=conversationId,SourcePurchaseIntentId=purchaseIntentId,Confidence=confidence,UpdatedAt=now,ExpiresAt=expiresAt,Version=existing.Version+1};
        _store.Save(item);return item;
    }
    public IReadOnlyList<ConsumerMemoryMatch> Retrieve(string principalId,string query,int maximum=8)
    {
        maximum=Math.Clamp(maximum,1,50);var terms=Terms(query);var matches=_store.ActiveForPrincipal(principalId,_clock()).Select(x=>new ConsumerMemoryMatch(x,Score(terms,x))).Where(x=>x.Score>0)
            .OrderByDescending(x=>x.Score).ThenByDescending(x=>x.Memory.UpdatedAt).Take(maximum).ToList();
        _store.RecordRetrieval(new($"cmra_{Guid.NewGuid():N}",principalId,query,matches.Select(x=>x.Memory.MemoryId).ToArray(),_clock()));return matches;
    }
    public async ValueTask<IReadOnlyList<ConsumerMemoryMatch>> RetrieveAsync(string principalId,string query,int maximum=8,CancellationToken cancellationToken=default)
    {
        if(_retriever is null)return Retrieve(principalId,query,maximum);
        var matches=await _retriever.RetrieveAsync(principalId,query,Math.Clamp(maximum,1,50),cancellationToken);
        _store.RecordRetrieval(new($"cmra_{Guid.NewGuid():N}",principalId,query,matches.Select(x=>x.Memory.MemoryId).ToArray(),_clock()));return matches;
    }
    public IReadOnlyList<ConsumerMemoryEntry> Export(string principalId)=>_store.ActiveForPrincipal(principalId,_clock()).OrderBy(x=>x.CreatedAt).ToList();
    public bool Delete(string principalId,string memoryId){var item=_store.FindOwned(memoryId,principalId);if(item is null)return false;_store.Delete(item with{Deleted=true,UpdatedAt=_clock(),Version=item.Version+1});return true;}
    public IReadOnlyList<ConsumerMemoryEntry> CaptureCorrections(string principal,string message,string? conversationId=null)
    {
        var found=new List<ConsumerMemoryEntry>();
        foreach(Match m in Regex.Matches(message,@"(?:don['’]?t|do not|never)\s+(?:buy|use)\s+(?<value>[^.!?]+)",RegexOptions.IgnoreCase))found.Add(Remember(principal,ConsumerMemoryKind.Correction,ConsumerMemoryPolarity.Negative,Normalize(m.Groups["value"].Value),m.Value,"explicit-user-correction",conversationId));
        foreach(Match m in Regex.Matches(message,@"\bI\s+(?:prefer|like)\s+(?<value>[^.!?]+)",RegexOptions.IgnoreCase))found.Add(Remember(principal,ConsumerMemoryKind.Preference,ConsumerMemoryPolarity.Positive,Normalize(m.Groups["value"].Value),m.Value,"explicit-user-preference",conversationId));
        foreach(Match m in Regex.Matches(message,@"\bI\s+already\s+have\s+(?<value>[^.!?]+)",RegexOptions.IgnoreCase))foreach(var value in Regex.Split(m.Groups["value"].Value,@"\s*,\s*|\s+and\s+",RegexOptions.IgnoreCase).Where(x=>!string.IsNullOrWhiteSpace(x)))found.Add(Remember(principal,ConsumerMemoryKind.Inventory,ConsumerMemoryPolarity.Positive,Normalize(value),$"Already has {value.Trim()}","explicit-user-inventory",conversationId,confidence:1,expiresAt:_clock().AddDays(7)));
        return found;
    }
    private static string Normalize(string value)=>Regex.Replace(value.Trim(),@"\s+again$",string.Empty,RegexOptions.IgnoreCase).Trim();
    private static HashSet<string> Terms(string value)=>Regex.Matches(value.ToLowerInvariant(),@"[a-z0-9]+").Select(x=>x.Value).Where(x=>x.Length>2).ToHashSet();
    private static double Score(HashSet<string> query,ConsumerMemoryEntry item){var memory=Terms($"{item.Subject} {item.Content}");if(query.Count==0||memory.Count==0)return 0;var overlap=query.Intersect(memory).Count();return overlap==0?0:(overlap/(double)query.Count*.7+overlap/(double)memory.Count*.3)*item.Confidence;}
}

public sealed class InMemoryConsumerMemoryStore:IConsumerMemoryStore
{
    private readonly object _gate=new();private readonly Dictionary<string,ConsumerMemoryEntry> _items=[];private readonly List<ConsumerMemoryRetrievalAudit> _audits=[];
    public void Save(ConsumerMemoryEntry x){lock(_gate)_items[x.MemoryId]=x;}
    public ConsumerMemoryEntry? FindOwned(string id,string principal){lock(_gate)return _items.GetValueOrDefault(id)is{} x&&x.PrincipalId==principal?x:null;}
    public IReadOnlyList<ConsumerMemoryEntry> ActiveForPrincipal(string principal,DateTimeOffset now){lock(_gate)return _items.Values.Where(x=>x.PrincipalId==principal&&!x.Deleted&&(x.ExpiresAt is null||x.ExpiresAt>now)).ToList();}
    public void Delete(ConsumerMemoryEntry x)=>Save(x);
    public void RecordRetrieval(ConsumerMemoryRetrievalAudit x){lock(_gate)_audits.Add(x);}
    public IReadOnlyList<ConsumerMemoryRetrievalAudit> Retrievals(string principal){lock(_gate)return _audits.Where(x=>x.PrincipalId==principal).ToList();}
}
