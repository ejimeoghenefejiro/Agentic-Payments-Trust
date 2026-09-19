using AgentTrust.Commerce;
using AgentTrust.Connectors;

namespace AgentTrust.Tests;

public sealed class RestaurantAndTaxiDomainTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();

    [Fact]
    public async Task Restaurant_NaturalLanguageGoal_UsesAuthoritativeQuoteAndExecutesOnce()
    {
        var authorisations = new HmacServiceActionAuthorisationService(Key);
        var connector = new DemoRestaurantConnector(authorisations);
        var router = new ServicePlanningRouter(new ServiceConnectorRegistry([connector]), [new RestaurantDomainCapability()]);
        var plan = await router.PlanAsync(new(
            "principal-1",
            "agent-1",
            "Order dinner for two under £30. One person is vegetarian. Choose good value and deliver it.",
            connector.ProviderId));
        var policy = new BoundedServiceActionTrustPolicy(
            new HashSet<string>([connector.ProviderId]),
            new HashSet<string>(["place_order"]));
        var orchestrator = new TrustedServiceActionOrchestrator(policy, authorisations);

        var first = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);
        var replay = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);

        Assert.True(first.Execution?.Succeeded);
        Assert.Equal(first.Execution?.ProviderReference, replay.Execution?.ProviderReference);
        Assert.Equal(1, connector.PlaceOrderCount);
        Assert.True(plan.Quote.Total <= 30);
        Assert.Equal("GBP", plan.Quote.Currency);
        Assert.Contains("get_restaurant_quote", plan.CapabilitiesUsed);
        Assert.DoesNotContain(connector.Capabilities.Where(x => x.Family != CapabilityFamily.Execution), x => x.Name == "place_order");
    }

    [Fact]
    public async Task Restaurant_TrustDenial_PreventsProviderOrder()
    {
        var authorisations = new HmacServiceActionAuthorisationService(Key);
        var connector = new DemoRestaurantConnector(authorisations);
        var plan = await new ServicePlanningRouter(new ServiceConnectorRegistry([connector]), [new RestaurantDomainCapability()])
            .PlanAsync(new("principal-1", "agent-1", "Order dinner for two under £10. One person is vegetarian. Deliver it.", connector.ProviderId));
        var policy = new BoundedServiceActionTrustPolicy(new HashSet<string>([connector.ProviderId]), new HashSet<string>(["place_order"]));

        var result = await new TrustedServiceActionOrchestrator(policy, authorisations)
            .ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);

        Assert.False(result.TrustDecision.Approved);
        Assert.Contains("AMOUNT_EXCEEDS_LIMIT", result.TrustDecision.Reasons);
        Assert.Equal(0, connector.PlaceOrderCount);
    }

    [Fact]
    public void Restaurant_ModifierValidation_RejectsMissingAndUnavailableSelections()
    {
        var group = new ModifierGroup("size", "Size", true, 1, 1,
            [new("regular", "Regular", 0, true), new("sold-out", "Sold out", 0, false)]);
        var item = new MenuItem("item", "restaurant", "Meal", "Meal", 5, "GBP", true,
            new HashSet<string>(), new HashSet<string>(), [group]);

        var missing = RestaurantModifierValidator.Validate(item, new("item", 1, []), new HashSet<string>());
        var unavailable = RestaurantModifierValidator.Validate(item, new("item", 1, ["sold-out"]), new HashSet<string>());

        Assert.Contains("MODIFIER_REQUIRED:size", missing);
        Assert.Contains("MODIFIER_UNAVAILABLE:size", unavailable);
    }

    [Fact]
    public async Task Restaurant_ProviderOwnsDeliveryAndServiceFees()
    {
        var connector = new DemoRestaurantConnector(new HmacServiceActionAuthorisationService(Key));
        var item = new RestaurantOrderItem("vegetable-bowl", 1, ["regular"]);

        var delivery = await connector.GetRestaurantQuoteAsync(new("restaurant-value", [item], RestaurantFulfilmentType.Delivery, "saved-address"));
        var pickup = await connector.GetRestaurantQuoteAsync(new("restaurant-value", [item], RestaurantFulfilmentType.Pickup));

        Assert.Equal(2.50m, delivery.DeliveryFee);
        Assert.Equal(0m, pickup.DeliveryFee);
        Assert.Equal(delivery.Subtotal + delivery.ModifierTotal + delivery.DeliveryFee + delivery.ServiceFee + delivery.Tax - delivery.Discount + delivery.Tip, delivery.Total);
    }

    [Fact]
    public async Task Taxi_NaturalLanguageGoal_ResolvesRouteQuotesAndBooksExactlyOnce()
    {
        var authorisations = new HmacServiceActionAuthorisationService(Key);
        var connector = new DemoTaxiConnector(authorisations);
        var router = new ServicePlanningRouter(new ServiceConnectorRegistry([connector]), [new TaxiDomainCapability()]);
        var plan = await router.PlanAsync(new(
            "principal-1",
            "agent-1",
            "Book me the cheapest suitable ride from Manchester Piccadilly to Manchester Airport. Do not spend more than £35.",
            connector.ProviderId));
        var policy = new BoundedServiceActionTrustPolicy(
            new HashSet<string>([connector.ProviderId]),
            new HashSet<string>(["book_ride"]));
        var orchestrator = new TrustedServiceActionOrchestrator(policy, authorisations);

        var first = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);
        var replay = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);

        Assert.True(first.Execution?.Succeeded);
        Assert.Equal(first.Execution?.ProviderReference, replay.Execution?.ProviderReference);
        Assert.Equal(1, connector.BookRideCount);
        Assert.True(plan.Quote.Total <= 35);
        Assert.Contains("resolve_location", plan.CapabilitiesUsed);
        Assert.Contains("get_ride_quote", plan.CapabilitiesUsed);
    }

    [Fact]
    public async Task Taxi_FiltersCapacityAndAccessibilityBeforeQuote()
    {
        var connector = new DemoTaxiConnector(new HmacServiceActionAuthorisationService(Key));
        var home = (await connector.ResolveLocationAsync("Saved home")).Single();
        var work = (await connector.ResolveLocationAsync("Saved work")).Single();

        var accessible = await connector.GetRideOptionsAsync(new(home, work, DateTimeOffset.UtcNow, 2, ["wheelchair"]));
        var sixSeats = await connector.GetRideOptionsAsync(new(home, work, DateTimeOffset.UtcNow, 6, []));

        Assert.All(accessible, option => Assert.True(option.WheelchairAccessible));
        Assert.All(sixSeats, option => Assert.True(option.Capacity >= 6));
    }

    [Fact]
    public async Task Taxi_OverBudgetTrustDenial_PreventsBooking()
    {
        var authorisations = new HmacServiceActionAuthorisationService(Key);
        var connector = new DemoTaxiConnector(authorisations);
        var plan = await new ServicePlanningRouter(new ServiceConnectorRegistry([connector]), [new TaxiDomainCapability()])
            .PlanAsync(new("principal-1", "agent-1", "Book a ride from Manchester Piccadilly to Manchester Airport under £10.", connector.ProviderId));
        var policy = new BoundedServiceActionTrustPolicy(new HashSet<string>([connector.ProviderId]), new HashSet<string>(["book_ride"]));

        var result = await new TrustedServiceActionOrchestrator(policy, authorisations)
            .ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);

        Assert.False(result.TrustDecision.Approved);
        Assert.Equal(0, connector.BookRideCount);
    }
}
