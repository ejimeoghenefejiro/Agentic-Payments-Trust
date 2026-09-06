using AgentTrust.Api;
using AgentTrust.Api.Controllers;
using AgentTrust.Commerce;
using AgentTrust.Connectors;

namespace AgentTrust.Tests;

public sealed class MerchantPlanningArchitectureTests
{
    [Fact]
    public async Task PlanningToolset_UsesMerchantQuoteAndExposesNoCheckoutCapability()
    {
        var connector=Connector();
        IMerchantPlanningToolset tools=new ConnectorMerchantPlanningToolset(connector,"principal-1","GBP",[new GroceryMealObjectiveCapability()]);

        var quote=await tools.QuoteAsync([new("bread",2)]);

        Assert.Equal(connector.MerchantId,tools.Context.MerchantId);
        Assert.Contains("quote_order",tools.Context.Capabilities);
        Assert.Equal(2.80m,quote.Subtotal);
        Assert.Equal(2.50m,quote.DeliveryFee);
        Assert.Equal(5.30m,quote.Total);
        Assert.Equal("GBP",quote.Currency);
        Assert.True(quote.ExpiresAt>DateTimeOffset.UtcNow);
        Assert.False(tools is ICheckoutCapability);
    }

    [Fact]
    public void ApiPlanningTypes_DoNotDependOnNamedMerchantPaymentOrTrustImplementations()
    {
        var forbidden=new[] { typeof(DemoGroceryConnector),typeof(StripePaymentAdapter) };
        var plannerDependencies=typeof(ConsumerPurchaseRequestAgent).GetConstructors().SelectMany(x=>x.GetParameters()).Select(x=>x.ParameterType)
            .Concat(typeof(ConsumerPurchaseRequestAgent).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Select(x=>x.FieldType)).ToArray();
        var controllerDependencies=typeof(ConsumerController).GetConstructors().SelectMany(x=>x.GetParameters()).Select(x=>x.ParameterType).ToArray();

        Assert.DoesNotContain(plannerDependencies.Concat(controllerDependencies),type=>forbidden.Contains(type));
        Assert.DoesNotContain(plannerDependencies,type=>typeof(ICheckoutCapability).IsAssignableFrom(type));
        Assert.DoesNotContain(plannerDependencies,type=>typeof(IPlatformPaymentProcessor).IsAssignableFrom(type));
    }

    [Fact]
    public void MerchantRegistry_ResolvesByStableMerchantIdAndRejectsDuplicates()
    {
        var connector=Connector();var registry=new MerchantConnectorRegistry([connector]);
        Assert.Same(connector,registry.GetRequired("grocerydemo"));
        Assert.Throws<InvalidOperationException>(()=>new MerchantConnectorRegistry([connector,Connector()]));
    }

    private static DemoGroceryConnector Connector()=>new(
        new HmacPurchaseAuthorisationService(Enumerable.Range(1,32).Select(x=>(byte)x).ToArray()),
        new MockPlatformPaymentProcessor());
}
