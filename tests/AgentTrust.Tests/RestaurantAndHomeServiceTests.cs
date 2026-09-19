using AgentTrust.Commerce;
using AgentTrust.Connectors;

namespace AgentTrust.Tests;

public sealed class RestaurantAndHomeServiceTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();

    [Fact]
    public async Task Restaurant_Demo_QuotesAndExecutesOnce()
    {
        var auth = new HmacServiceActionAuthorisationService(Key); var connector = new DemoRestaurantConnector(auth);
        var plan = await new ServicePlanningRouter(new ServiceConnectorRegistry([connector]), [new RestaurantDomainCapability()]).PlanAsync(new("p", "a", "Order dinner for two under £30. One person is vegetarian. Choose good value and deliver it.", connector.ProviderId));
        var orchestrator = new TrustedServiceActionOrchestrator(Policy(connector.ProviderId, "place_order"), auth);
        var first = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow); var replay = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);
        Assert.True(first.Execution?.Succeeded); Assert.Equal(first.Execution?.ProviderReference, replay.Execution?.ProviderReference); Assert.Equal(1, connector.PlaceOrderCount); Assert.True(plan.Quote.Total <= 30);
    }

    [Fact]
    public void Restaurant_RejectsMissingAndUnavailableModifiers()
    {
        var group = new ModifierGroup("size", "Size", true, 1, 1, [new("regular", "Regular", 0, true), new("sold-out", "Sold out", 0, false)]);
        var item = new MenuItem("item", "restaurant", "Meal", "Meal", 5, "GBP", true,
            new HashSet<string>(), new HashSet<string>(), [group]);
        Assert.Contains("MODIFIER_REQUIRED:size", RestaurantModifierValidator.Validate(item, new("item", 1, []), new HashSet<string>()));
        Assert.Contains("MODIFIER_UNAVAILABLE:size", RestaurantModifierValidator.Validate(item, new("item", 1, ["sold-out"]), new HashSet<string>()));
    }

    [Fact]
    public async Task HomeService_DiscoversAvailabilityAndUsesProviderQuote()
    {
        var connector = new DemoHomeServiceConnector(new HmacServiceActionAuthorisationService(Key));
        var services = await connector.SearchServicesAsync(new("cleaner", "default-area"));
        var hourly = services.Single(x => x.PricingType == ServicePricingType.Hourly);
        var slots = await connector.GetAvailabilityAsync(new(hourly.ServiceId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "morning", TimeSpan.FromHours(3), "saved-home"));
        var quote = await connector.GetHomeServiceQuoteAsync(new(hourly.ProviderId, hourly.ServiceId, slots.Single(x => x.Available).SlotId, TimeSpan.FromHours(3), "saved-home", []));
        Assert.Equal(48m, quote.LabourAmount); Assert.Equal(50.40m, quote.Total); Assert.Contains(slots, x => !x.Available);
    }

    [Fact]
    public async Task HomeService_EndToEnd_BooksExactlyOnce()
    {
        var auth = new HmacServiceActionAuthorisationService(Key); var connector = new DemoHomeServiceConnector(auth);
        var plan = await new ServicePlanningRouter(new ServiceConnectorRegistry([connector]), [new HomeServiceDomainCapability()]).PlanAsync(new("p", "a", "Find me a cleaner for Saturday morning for three hours. Do not spend more than £60.", connector.ProviderId));
        var orchestrator = new TrustedServiceActionOrchestrator(Policy(connector.ProviderId, "book_service"), auth);
        var first = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow); var replay = await orchestrator.ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);
        Assert.True(first.Execution?.Succeeded); Assert.Equal(first.Execution?.ProviderReference, replay.Execution?.ProviderReference); Assert.Equal(1, connector.BookingCount); Assert.True(plan.Quote.Total <= 60);
    }

    [Fact]
    public async Task HomeService_OverBudgetTrustDenial_PreventsBooking()
    {
        var auth = new HmacServiceActionAuthorisationService(Key); var connector = new DemoHomeServiceConnector(auth);
        var plan = await new ServicePlanningRouter(new ServiceConnectorRegistry([connector]), [new HomeServiceDomainCapability()]).PlanAsync(new("p", "a", "Find me a cleaner for Saturday morning for three hours under £20.", connector.ProviderId));
        var result = await new TrustedServiceActionOrchestrator(Policy(connector.ProviderId, "book_service"), auth).ExecuteAsync(plan.Proposal, plan.Quote, connector, DateTimeOffset.UtcNow);
        Assert.False(result.TrustDecision.Approved, $"quote={plan.Quote.Total}, maximum={plan.Proposal.MaximumAmount}"); Assert.Equal(0, connector.BookingCount);
    }

    [Fact]
    public async Task HomeService_CancellationAndRescheduleRequireMatchingSignedAction()
    {
        var auth = new HmacServiceActionAuthorisationService(Key); var connector = new DemoHomeServiceConnector(auth);
        var invalid = new ServiceActionProposal("p", "p", "a", connector.ProviderId, "book_service", "x", System.Text.Json.JsonSerializer.SerializeToElement(new { }), 10, "GBP", "k");
        var quote = new ServiceQuote("q", connector.ProviderId, "book_service", 10, "GBP", DateTimeOffset.UtcNow.AddMinutes(2), System.Text.Json.JsonSerializer.SerializeToElement(new { }));
        var bookingAuth = auth.Issue(invalid, quote, "v1", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.CancelServiceAsync("missing", bookingAuth));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.RescheduleServiceAsync("missing", "slot", bookingAuth));
    }

    private static BoundedServiceActionTrustPolicy Policy(string provider, string action) => new(new HashSet<string>([provider]), new HashSet<string>([action]));
}
