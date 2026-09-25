using AgentTrust.Api;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.Mandates;
using AgentTrust.PaymentMethods;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentTrust.Tests;

public sealed class Level5AutonomyPromotionTests
{
    [Theory]
    [InlineData(0.999, 1.0, "SAFETY_MUST_BE_100_PERCENT")]
    [InlineData(1.0, 0.999, "CUSTOMER_ISOLATION_MUST_BE_100_PERCENT")]
    public void PromotionFailsBelowPerfectSafetyOrIsolation(double safety,double isolation,string failure)
    {
        var result=new AutonomyPromotionGate().Evaluate(new((decimal)safety,(decimal)isolation,
            [new("all-required-scenarios",true,"verified")]));

        Assert.False(result.Promoted);
        Assert.Contains(failure,result.Failures);
    }

    [Fact]
    public void PromotionRequiresEveryNamedScenarioToPass()
    {
        var result=new AutonomyPromotionGate().Evaluate(new(1m,1m,
            [new("payment-timeout-recovery",true,"one logical charge"),
             new("fulfilment-evidence",false,"missing provider confirmation")]));

        Assert.False(result.Promoted);
        Assert.Contains("SCENARIO_FAILED:fulfilment-evidence",result.Failures);
    }

    [Fact]
    public async Task OperatorRejectsUnauditedAndModifiedProposalsBeforeExecution()
    {
        var connector=new QuotingConnector("store-a",1m);
        var registry=new MerchantConnectorRegistry([connector]);
        var operatorWorker=new ConsumerCommerceOperator(null!,null!,registry,new ConfigurationBuilder().Build());
        var quote=await Quote(connector);
        var plan=Plan([]);
        var preparation=new ConsumerCommercePreparation(plan,quote,Mandate(),Method(),[]);

        var missing=await Assert.ThrowsAsync<InvalidOperationException>(()=>operatorWorker.ExecuteAsync(preparation,null!,"principal",default));
        Assert.Equal("OPERATOR_AUDIT_ACCEPTANCE_REQUIRED",missing.Message);

        var accepted=Plan(["auditor:accepted"]);
        var hash=CommerceProposalFingerprint.Hash(accepted,quote);
        accepted=accepted with{ToolsUsed=["auditor:accepted",$"auditor:proposal-hash:{hash}"]};
        var modified=accepted with{MaximumAmount=accepted.MaximumAmount+1};
        var mismatch=await Assert.ThrowsAsync<InvalidOperationException>(()=>operatorWorker.ExecuteAsync(
            new(modified,quote,Mandate(),Method(),[]),null!,"principal",default));
        Assert.Equal("OPERATOR_AUDITED_PROPOSAL_MISMATCH",mismatch.Message);
    }

    [Fact]
    public async Task ProviderOptimizerSelectsBestCompleteAuthoritativeQuote()
    {
        var expensive=new QuotingConnector("store-expensive",4m);
        var value=new QuotingConnector("store-value",2m);
        var optimizer=new CommerceProviderOptimizer([]);

        var result=await optimizer.QuoteBestAsync("principal",[expensive,value],
            [new("bread",1)],10m,"GBP");

        Assert.Equal("store-value",result.BestQuote.MerchantId);
        Assert.Equal(2,result.Evaluations.Count);
        Assert.All(result.Evaluations,x=>Assert.Null(x.RejectionReason));
    }

    [Fact]
    public void OodaStepsAreAppendOnlyOrderedAndPrincipalIsolated()
    {
        var store=new InMemoryCommerceOodaCycleStore();var now=DateTimeOffset.UtcNow;
        store.Save(new("cycle","task","alice","purchase",now,1,CommerceOodaStatus.Observing,"[]","[]","[]","{}","{}","{}",null,now,now));
        store.Append(new("step-1","cycle","alice",1,1,CommerceOodaStatus.Observing,"{}","{}","{}",null,now));
        store.Append(new("step-2","cycle","alice",1,2,CommerceOodaStatus.Orienting,"{}","{}","{}",null,now));
        store.Append(new("step-1","cycle","alice",1,3,CommerceOodaStatus.Deciding,"{}","{}","{}",null,now));

        var steps=store.StepsOwned("cycle","alice");
        Assert.Equal(2,steps.Count);
        Assert.Equal([CommerceOodaStatus.Observing,CommerceOodaStatus.Orienting],steps.Select(x=>x.Phase));
        Assert.Empty(store.StepsOwned("cycle","bob"));
        Assert.Null(store.FindOwned("purchase","bob"));
    }

    private static ConsumerPurchasePlan Plan(IReadOnlyList<string> tools)=>new(PurchasePlanningStatus.Ready,"buy bread","ready",10,"GBP",
        [new("bread",1)],[],2,tools,"conversation",1,PurchaseInteractionDecision.Propose);
    private static FinancialMandate Mandate()=>new("mandate","principal","agent","store-a","groceries","method",10,null,null,"GBP",
        new Dictionary<string,string>(),AboveLimitAction.Block,MandateStatus.Active,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(1));
    private static PaymentMethod Method()=>new("method","principal","Mock","token","Visa","4242",12,2035,PaymentMethodStatus.Active);
    private static async Task<MerchantPlanningQuote> Quote(ICommerceConnector connector)=>await new ConnectorMerchantPlanningToolset(connector,"principal","GBP").QuoteAsync([new("bread",1)]);

    private sealed class QuotingConnector(string merchantId,decimal price):ICommerceConnector
    {
        private readonly Dictionary<string,List<BasketItem>> _baskets=[];
        public string MerchantId=>merchantId;public string MerchantName=>merchantId;
        public Task<IReadOnlyList<Product>> SearchProductsAsync(string query,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<Product>>([new("bread","Bread",price,"GBP",10,new HashSet<string>{"bread"})]);
        public Task<Product?> GetProductAsync(string id,CancellationToken ct=default)=>Task.FromResult<Product?>(new("bread","Bread",price,"GBP",10,new HashSet<string>{"bread"}));
        public Task<Basket> CreateBasketAsync(string principal,CancellationToken ct=default){var id=Guid.NewGuid().ToString("N");_baskets[id]=[];return Task.FromResult(new Basket(id,MerchantId,[]));}
        public Task<Basket> AddBasketItemAsync(string id,string productId,int quantity,bool substitutions,CancellationToken ct=default){_baskets[id].Add(new(productId,"Bread",quantity,price,price*quantity,substitutions));return Task.FromResult(new Basket(id,MerchantId,_baskets[id]));}
        public Task<Basket> RemoveBasketItemAsync(string id,string productId,CancellationToken ct=default){_baskets[id].RemoveAll(x=>x.ProductId==productId);return Task.FromResult(new Basket(id,MerchantId,_baskets[id]));}
        public Task<Basket> GetBasketAsync(string id,CancellationToken ct=default)=>Task.FromResult(new Basket(id,MerchantId,_baskets[id]));
        public Task<IReadOnlyList<DeliveryOption>> GetDeliveryOptionsAsync(string id,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<DeliveryOption>>([new("delivery","Delivery",0,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddHours(1))]);
        public Task SelectDeliveryOptionAsync(string id,string option,CancellationToken ct=default)=>Task.CompletedTask;
        public Task<CommerceQuote> GetQuoteAsync(string id,string option,CancellationToken ct=default){var items=_baskets[id];return Task.FromResult(new CommerceQuote(Guid.NewGuid().ToString("N"),id,MerchantId,MerchantName,"GBP",items,items.Sum(x=>x.TotalPrice),0,items.Sum(x=>x.TotalPrice),option,DateTimeOffset.UtcNow.AddMinutes(5)));}
        public Task PrepareCheckoutAsync(PurchaseIntent intent,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<ConnectorPurchaseResult> ExecutePurchaseAsync(PurchaseIntent intent,PurchaseAuthorisation auth,CancellationToken ct=default)=>throw new NotSupportedException();
    }
}
