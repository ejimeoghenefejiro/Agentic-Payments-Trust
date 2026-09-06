using AgentTrust.Consumer;
using AgentTrust.Data;
using AgentTrust.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace AgentTrust.Tests.Consumer;

public sealed class ConsumerMemoryServiceTests
{
    private static readonly DateTimeOffset Now=new(2026,9,5,12,0,0,TimeSpan.Zero);

    [Fact]
    public void RetrievalIsStrictlyPrincipalScopedAndAudited()
    {
        var store=new InMemoryConsumerMemoryStore();var service=new ConsumerMemoryService(store,()=>Now);
        service.Remember("alice",ConsumerMemoryKind.Preference,ConsumerMemoryPolarity.Positive,"wrap brand","Prefers value wholemeal wraps","explicit-user-preference");
        service.Remember("bob",ConsumerMemoryKind.Preference,ConsumerMemoryPolarity.Positive,"wrap brand","Prefers premium white wraps","explicit-user-preference");

        var results=service.Retrieve("alice","usual value wraps");

        var result=Assert.Single(results);Assert.Equal("alice",result.Memory.PrincipalId);Assert.Contains("wholemeal",result.Memory.Content);
        var audit=Assert.Single(store.Retrievals("alice"));Assert.Equal([result.Memory.MemoryId],audit.ReturnedMemoryIds);
        Assert.Empty(store.Retrievals("bob"));
    }

    [Fact]
    public void CorrectionsCapturePositiveNegativeAndExpiringInventoryMemory()
    {
        var service=new ConsumerMemoryService(new InMemoryConsumerMemoryStore(),()=>Now);
        service.CaptureCorrections("alice","I prefer free-range chicken. Don't buy that garlic sauce again. I already have tomatoes and sauce.","conversation-1");

        var export=service.Export("alice");
        Assert.Contains(export,x=>x.Kind==ConsumerMemoryKind.Preference&&x.Polarity==ConsumerMemoryPolarity.Positive);
        Assert.Contains(export,x=>x.Kind==ConsumerMemoryKind.Correction&&x.Polarity==ConsumerMemoryPolarity.Negative&&x.Provenance=="explicit-user-correction");
        Assert.Equal(2,export.Count(x=>x.Kind==ConsumerMemoryKind.Inventory&&x.ExpiresAt==Now.AddDays(7)));
    }

    [Fact]
    public void OwnerCanDeleteAndAnotherPrincipalCannot()
    {
        var service=new ConsumerMemoryService(new InMemoryConsumerMemoryStore(),()=>Now);
        var item=service.Remember("alice",ConsumerMemoryKind.Accessibility,ConsumerMemoryPolarity.Positive,"response style","voice-friendly short summaries","explicit-user-preference");
        Assert.False(service.Delete("bob",item.MemoryId));Assert.True(service.Delete("alice",item.MemoryId));Assert.Empty(service.Export("alice"));
    }

    [Fact]
    public void ExpiredMemoryIsNeverRetrievedOrExported()
    {
        var service=new ConsumerMemoryService(new InMemoryConsumerMemoryStore(),()=>Now);
        service.Remember("alice",ConsumerMemoryKind.Inventory,ConsumerMemoryPolarity.Positive,"tomatoes","Already has tomatoes","explicit-user-inventory",expiresAt:Now.AddSeconds(-1));
        Assert.Empty(service.Retrieve("alice","tomatoes"));Assert.Empty(service.Export("alice"));
    }

    [Fact]
    public void MemoryEnabledB3RetrievesAcceptedSubstitutionWhileB2DoesNot()
    {
        var service=new ConsumerMemoryService(new InMemoryConsumerMemoryStore(),()=>Now);
        service.Remember("alice",ConsumerMemoryKind.Substitution,ConsumerMemoryPolarity.Positive,"tortilla substitution","Accepted wholemeal wraps when usual wraps unavailable","analyst-confirmed-purchase");
        IReadOnlyList<ConsumerMemoryMatch> b2=[];var b3=service.Retrieve("alice","usual tortilla wraps unavailable substitution");
        Assert.Empty(b2);Assert.NotEmpty(b3);Assert.True(b3[0].Score>0);
    }

    [Fact]
    public async Task SemanticRetrieverRejectsCrossCustomerVectorHitsEvenIfIndexMisbehaves()
    {
        var store=new InMemoryConsumerMemoryStore();var service=new ConsumerMemoryService(store,()=>Now);
        var alice=service.Remember("alice",ConsumerMemoryKind.Preference,ConsumerMemoryPolarity.Positive,"wraps","value wraps","test");
        var bob=service.Remember("bob",ConsumerMemoryKind.Preference,ConsumerMemoryPolarity.Positive,"wraps","premium wraps","test");
        var retriever=new SemanticConsumerMemoryRetriever(store,new FixedEmbedding(),new LeakyVectorIndex(alice.MemoryId,bob.MemoryId),new InMemoryConsumerMemoryCache(),clock:()=>Now);

        var result=await retriever.RetrieveAsync("alice","wraps",10);

        var match=Assert.Single(result);Assert.Equal(alice.MemoryId,match.Memory.MemoryId);Assert.DoesNotContain(result,x=>x.Memory.PrincipalId=="bob");
    }

    [Fact]
    public void EfSaveAtomicallyCreatesVectorIndexOutboxWork()
    {
        var options=new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db=new AgentTrustDbContext(options);db.Database.OpenConnection();db.Database.EnsureCreated();var store=new EfConsumerMemoryStore(db);
        store.Save(new("memory-1","alice",ConsumerMemoryKind.Preference,ConsumerMemoryPolarity.Positive,"wraps","value wraps","explicit",null,null,1,Now,Now));
        var row=Assert.Single(db.ConsumerMemoryOutbox);Assert.Equal("memory-1",row.MemoryId);Assert.Equal("alice",row.PrincipalId);Assert.Equal("Upsert",row.Operation);Assert.Equal("Pending",row.Status);
    }

    [Fact]
    public async Task QdrantSearchAlwaysSendsPrincipalPayloadFilter()
    {
        var handler=new RecordingHandler();var client=new HttpClient(handler){BaseAddress=new Uri("http://qdrant/")};var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConsumerMemory:Qdrant:Collection","consumer_memories"}}).Build();var index=new QdrantConsumerMemoryVectorIndex(client,new FixedEmbedding(),configuration);
        await index.SearchAsync("alice",new float[]{1,0},5);
        Assert.Contains("\"key\":\"principal_id\"",handler.QueryBody);Assert.Contains("\"value\":\"alice\"",handler.QueryBody);
    }

    private sealed class FixedEmbedding:IConsumerMemoryEmbeddingService{public int Dimensions=>2;public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text,CancellationToken token=default)=>ValueTask.FromResult<ReadOnlyMemory<float>>(new float[]{1,0});}
    private sealed class LeakyVectorIndex(params string[] ids):IConsumerMemoryVectorIndex
    {
        public ValueTask UpsertAsync(ConsumerMemoryEntry memory,ReadOnlyMemory<float> embedding,CancellationToken token=default)=>ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string principal,string memoryId,CancellationToken token=default)=>ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<ConsumerMemoryVectorHit>> SearchAsync(string principal,ReadOnlyMemory<float> query,int maximum,CancellationToken token=default)=>ValueTask.FromResult<IReadOnlyList<ConsumerMemoryVectorHit>>(ids.Select((id,index)=>new ConsumerMemoryVectorHit(id,1-index*.1)).ToList());
    }
    private sealed class RecordingHandler:HttpMessageHandler
    {
        public string QueryBody{get;private set;}="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Post)QueryBody=await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK){Content=new StringContent(request.Method==HttpMethod.Post?"{\"result\":{\"points\":[]}}":"{}")};
        }
    }
}
