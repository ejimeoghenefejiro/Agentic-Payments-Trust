using AgentTrust.Commerce;
using AgentTrust.Connectors;

namespace AgentTrust.Tests;

public sealed class RestaurantTests
{
    private static readonly byte[] Key=Enumerable.Range(1,32).Select(x=>(byte)x).ToArray();

    [Fact]
    public async Task Restaurant_Demo_QuotesAndExecutesOnce()
    {
        var auth=new HmacServiceActionAuthorisationService(Key);var connector=new DemoRestaurantConnector(auth);
        var plan=await new ServicePlanningRouter(new ServiceConnectorRegistry([connector]),[new RestaurantDomainCapability()])
            .PlanAsync(new("p","a","Order dinner for two under £30. One person is vegetarian. Choose good value and deliver it.",connector.ProviderId));
        var orchestrator=new TrustedServiceActionOrchestrator(Policy(connector.ProviderId,"place_order"),auth);

        var first=await orchestrator.ExecuteAsync(plan.Proposal,plan.Quote,connector,DateTimeOffset.UtcNow);
        var replay=await orchestrator.ExecuteAsync(plan.Proposal,plan.Quote,connector,DateTimeOffset.UtcNow);

        Assert.True(first.Execution?.Succeeded);
        Assert.Equal(first.Execution?.ProviderReference,replay.Execution?.ProviderReference);
        Assert.Equal(1,connector.PlaceOrderCount);
        Assert.True(plan.Quote.Total<=30);
    }

    [Fact]
    public void Restaurant_RejectsMissingAndUnavailableModifiers()
    {
        var group=new ModifierGroup("size","Size",true,1,1,[new("regular","Regular",0,true),new("sold-out","Sold out",0,false)]);
        var item=new MenuItem("item","restaurant","Meal","Meal",5,"GBP",true,new HashSet<string>(),new HashSet<string>(),[group]);

        Assert.Contains("MODIFIER_REQUIRED:size",RestaurantModifierValidator.Validate(item,new("item",1,[]),new HashSet<string>()));
        Assert.Contains("MODIFIER_UNAVAILABLE:size",RestaurantModifierValidator.Validate(item,new("item",1,["sold-out"]),new HashSet<string>()));
    }

    private static BoundedServiceActionTrustPolicy Policy(string provider,string action)=>
        new(new HashSet<string>([provider]),new HashSet<string>([action]));
}
