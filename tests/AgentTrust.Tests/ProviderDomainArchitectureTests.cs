using System.Text.Json;
using AgentTrust.Api;
using AgentTrust.Api.Controllers;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using Microsoft.Extensions.Configuration;

namespace AgentTrust.Tests;

public sealed class ProviderDomainArchitectureTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();

    [Fact]
    public async Task GroceryPlanning_UsesProviderQuoteAndCannotCheckout()
    {
        var connector = Grocery();
        IMerchantPlanningToolset tools = new ConnectorMerchantPlanningToolset(connector, "principal-1", "GBP", [new GroceryMealObjectiveCapability()]);
        var quote = await tools.QuoteAsync([new("bread", 2)]);
        Assert.Equal(5.30m, quote.Total);
        Assert.False(tools is ICheckoutCapability);
    }

    [Fact]
    public void GenericAgent_HasNoConcreteDomainOrExecutionDependencies()
    {
        var forbidden = new[] { typeof(DemoGroceryConnector), typeof(DemoRestaurantConnector), typeof(MenuItem), typeof(IPlatformPaymentProcessor) };
        var dependencies = typeof(ConsumerPurchaseRequestAgent).GetConstructors().SelectMany(x => x.GetParameters()).Select(x => x.ParameterType)
            .Concat(typeof(ConsumerPurchaseRequestAgent).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Select(x => x.FieldType)).ToArray();
        Assert.DoesNotContain(dependencies, type => forbidden.Contains(type));
        var parameters = typeof(IConsumerPurchaseRequestAgent).GetMethod(nameof(IConsumerPurchaseRequestAgent.PlanAsync))!.GetParameters().Select(x => x.ParameterType);
        Assert.DoesNotContain(parameters, type => forbidden.Contains(type) || type == typeof(Product));
    }

    [Fact]
    public void ServiceAuthorisation_MalformedSignature_IsRejected()
    {
        var service = new HmacServiceActionAuthorisationService(Key);
        var proposal = new ServiceActionProposal("p", "principal", "agent", "provider", "book", "objective", JsonSerializer.SerializeToElement(new { }), 10, "GBP", "key");
        var quote = new ServiceQuote("q", "provider", "book", 10, "GBP", DateTimeOffset.UtcNow.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
        var auth = service.Issue(proposal, quote, "v1", DateTimeOffset.UtcNow) with { Signature = "not-hex" };
        Assert.False(service.Verify(auth, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MerchantRegistry_RejectsDuplicateProviderIds()
    {
        var connector = Grocery();
        Assert.Throws<InvalidOperationException>(() => new MerchantConnectorRegistry([connector, Grocery()]));
    }

    [Fact]
    public async Task CommerceAgentLoop_DiscoversQuotesAndPreparesWithoutPaymentCapability()
    {
        var connector=Grocery();var store=new InMemoryConsumerPlanningStore();
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {{"ConsumerPilot:Planning:AgentLoopMaximumTurns","4"}}).Build();
        var loop=new ConsumerCommerceAgentLoop(store,new TestAnalyst(),new BreakfastPlanner(),new AcceptingAuditor(),[],configuration);
        var request=new ConsumerActionPlanningContext("principal-1",null,"Find a good-value breakfast within £10.50",
            connector.MerchantId,connector.MerchantName,CommerceCapabilityCatalog.Describe(connector));
        var provider=new ProviderPlanningSession(connector.MerchantId,connector.MerchantName,
            CommerceCapabilityCatalog.Describe(connector),connector);

        var plan=await loop.RunAsync(request,provider,CancellationToken.None);

        Assert.Equal(PurchasePlanningStatus.Ready,plan.Status);Assert.Equal(PurchaseInteractionDecision.Propose,plan.InteractionDecision);
        Assert.Equal(9.90m,plan.EstimatedTotal);Assert.Equal(4,plan.Items.Count);
        Assert.Contains("analyst:get_customer_context",plan.ToolsUsed);Assert.Contains("analyst:discover_providers",plan.ToolsUsed);Assert.Contains("search",plan.ToolsUsed);
        Assert.Contains("compare_options",plan.ToolsUsed);Assert.Contains("get_authoritative_quote",plan.ToolsUsed);
        Assert.Contains("check_execution_readiness",plan.ToolsUsed);Assert.Contains("prepare_purchase",plan.ToolsUsed);
        Assert.Contains("planner:reason",plan.ToolsUsed);Assert.Contains("auditor:accepted",plan.ToolsUsed);
        Assert.DoesNotContain(typeof(ICheckoutCapability),typeof(ConsumerCommerceAgentLoop).GetInterfaces());

        var confirmed=await loop.RunAsync(request with{ConversationId=plan.ConversationId,Instruction="Yes"},provider,CancellationToken.None);
        Assert.Equal(PurchaseInteractionDecision.Execute,confirmed.InteractionDecision);
    }

    private sealed class TestAnalyst:ICommerceAnalystWorker
    {
        public Task<CustomerRequestUnderstanding> AnalyzeAsync(string instruction,string? openObjective,IReadOnlySet<string> capabilities,CancellationToken cancellationToken)=>
            Task.FromResult(CustomerRequestUnderstandingFallback.Parse(instruction,openObjective));
    }
    private sealed class BreakfastPlanner:ICommercePlannerWorker
    {
        public Task<CommerceAgentDecision> DecideAsync(CommerceAgentContext context,CancellationToken cancellationToken)=>
            Task.FromResult(context.Candidates.Count==0
                ?new CommerceAgentDecision(["bread","eggs","milk","banana"],[],"Discover breakfast","Searching the provider.")
                :new CommerceAgentDecision([],context.Candidates.Select(x=>new CommerceAgentItem(x.ProductId)).ToArray(),"Best-value breakfast","A complete breakfast is ready."));
    }
    private sealed class AcceptingAuditor:ICommerceAuditorWorker
    {
        public Task<CommerceAuditResult> AuditAsync(CommerceAgentContext context,CommerceAgentDecision decision,MerchantPlanningQuote quote,CancellationToken cancellationToken)=>
            Task.FromResult(new CommerceAuditResult(true,false,[],null));
    }

    private static DemoGroceryConnector Grocery() => new(new HmacPurchaseAuthorisationService(Key), new MockPlatformPaymentProcessor());
}
