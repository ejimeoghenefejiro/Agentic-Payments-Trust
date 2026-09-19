using System.Text.Json;
using AgentTrust.Api;
using AgentTrust.Api.Controllers;
using AgentTrust.Commerce;
using AgentTrust.Connectors;

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
        var forbidden = new[] { typeof(DemoGroceryConnector), typeof(DemoRestaurantConnector), typeof(DemoHomeServiceConnector), typeof(MenuItem), typeof(HomeServiceOffering), typeof(IPlatformPaymentProcessor) };
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

    private static DemoGroceryConnector Grocery() => new(new HmacPurchaseAuthorisationService(Key), new MockPlatformPaymentProcessor());
}
