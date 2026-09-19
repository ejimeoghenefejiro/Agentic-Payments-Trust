using AgentTrust.Api;
using AgentTrust.Api.Controllers;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using System.Text.Json;

namespace AgentTrust.Tests;

public sealed class MerchantPlanningArchitectureTests
{
    [Fact]
    public async Task PlanningToolset_UsesMerchantQuoteAndExposesNoCheckoutCapability()
    {
        var connector = Connector();
        IMerchantPlanningToolset tools = new ConnectorMerchantPlanningToolset(connector, "principal-1", "GBP", [new GroceryMealObjectiveCapability()]);

        var quote = await tools.QuoteAsync([new("bread", 2)]);

        Assert.Equal(connector.MerchantId, tools.Context.MerchantId);
        Assert.Contains("quote_order", tools.Context.Capabilities);
        Assert.Equal(2.80m, quote.Subtotal);
        Assert.Equal(2.50m, quote.DeliveryFee);
        Assert.Equal(5.30m, quote.Total);
        Assert.Equal("GBP", quote.Currency);
        Assert.True(quote.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.False(tools is ICheckoutCapability);
    }

    [Fact]
    public void ApiPlanningTypes_DoNotDependOnNamedMerchantPaymentOrTrustImplementations()
    {
        var forbidden = new[] { typeof(DemoGroceryConnector), typeof(StripePaymentAdapter) };
        var plannerDependencies = typeof(ConsumerPurchaseRequestAgent).GetConstructors().SelectMany(x => x.GetParameters()).Select(x => x.ParameterType)
            .Concat(typeof(ConsumerPurchaseRequestAgent).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Select(x => x.FieldType)).ToArray();
        var controllerDependencies = typeof(ConsumerController).GetConstructors().SelectMany(x => x.GetParameters()).Select(x => x.ParameterType).ToArray();

        Assert.DoesNotContain(plannerDependencies.Concat(controllerDependencies), type => forbidden.Contains(type));
        Assert.DoesNotContain(plannerDependencies, type => typeof(ICheckoutCapability).IsAssignableFrom(type));
        Assert.DoesNotContain(plannerDependencies, type => typeof(IPlatformPaymentProcessor).IsAssignableFrom(type));
        var contractParameters = typeof(IConsumerPurchaseRequestAgent).GetMethod(nameof(IConsumerPurchaseRequestAgent.PlanAsync))!.GetParameters().Select(x => x.ParameterType).ToArray();
        Assert.DoesNotContain(contractParameters, type => type == typeof(Product) || type == typeof(IReadOnlyList<Product>));
    }

    [Fact]
    public async Task GenericAgent_DispatchesToRegisteredProviderCapabilityWithoutCatalogueKnowledge()
    {
        var hotel = new StubProviderCapability("hotel-x"); var domain = new StubDomainCapability();
        var agent = new ConsumerPurchaseRequestAgent([hotel], [domain]);
        var context = new ConsumerActionPlanningContext("principal-1", null, "Book an accessible room", "hotel-x", "Hotel X",
            new HashSet<string>(["search_rooms", "get_quote"]));

        var plan = await agent.PlanAsync(context, CancellationToken.None);

        Assert.Equal("hotel-x", domain.Received?.ProviderId);
        Assert.Equal("Book an accessible room", plan.Summary);
        Assert.Equal("EUR", plan.Currency);
    }

    [Fact]
    public async Task HotelService_NaturalLanguageToAuthorisedExecution_UsesNoProductsOrBaskets()
    {
        var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        var authorisations = new HmacServiceActionAuthorisationService(key); IServiceConnector hotel = new DemoHotelConnector(authorisations);
        var router = new ServicePlanningRouter(new ServiceConnectorRegistry([hotel]), [new HotelDomainCapability()]);
        var plan = await router.PlanAsync(new("principal-1", "agent-1", "Book an accessible hotel room in London from 2026-12-10 to 2026-12-11 for 2 guests under £100", "hotel-demo"));
        var policy = new BoundedServiceActionTrustPolicy(new HashSet<string>(["hotel-demo"]), new HashSet<string>(["book"]));
        var result = await new TrustedServiceActionOrchestrator(policy, authorisations).ExecuteAsync(plan.Proposal, plan.Quote, hotel, DateTimeOffset.UtcNow);

        Assert.Equal(["search", "get_quote"], plan.CapabilitiesUsed);
        Assert.Equal(89m, plan.Quote.Total); Assert.Equal("GBP", plan.Quote.Currency);
        Assert.True(result.TrustDecision.Approved); Assert.NotNull(result.Authorisation);
        Assert.True(result.Execution?.Succeeded); Assert.StartsWith("hotel_", result.Execution?.ProviderReference);
        Assert.DoesNotContain(hotel.Capabilities, x => x.Name.Contains("product") || x.Name.Contains("basket"));
        var confirmation = await hotel.InvokeAsync("confirmation", System.Text.Json.JsonSerializer.SerializeToElement(new { providerReference = result.Execution!.ProviderReference })); Assert.True(confirmation.Success); Assert.Equal("Confirmed", confirmation.Value.GetProperty("status").GetString());
        var cancelled = await hotel.InvokeAsync("cancel", System.Text.Json.JsonSerializer.SerializeToElement(new { providerReference = result.Execution.ProviderReference })); Assert.True(cancelled.Success); Assert.Equal("pending_policy_evaluation", cancelled.Value.GetProperty("refundStatus").GetString());
    }

    [Fact]
    public async Task HotelService_RejectsMissingDatesBeforeProviderCalls()
    {
        var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray(); IServiceConnector hotel = new DemoHotelConnector(new HmacServiceActionAuthorisationService(key));
        var router = new ServicePlanningRouter(new ServiceConnectorRegistry([hotel]), [new HotelDomainCapability()]);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => router.PlanAsync(new("principal-1", "agent-1", "Book a room in London under £100", "hotel-demo")));
        Assert.Contains("YYYY-MM-DD", error.Message);
    }

    [Fact]
    public void HotelStore_EnforcesReplayLookupAndMaintainsHashChain()
    {
        IHotelBookingStore store = new InMemoryHotelBookingStore(); var now = DateTimeOffset.UtcNow; var booking = new HotelBooking("b1", "p1", "a1", "m1", "pm1", "hotel-demo", "objective", "room", new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 11), 2, true, 89, "GBP", "q1", null, null, null, "stable-key", HotelBookingStatus.Quoted, now, now);
        Assert.True(store.TryCreateBooking(booking, out var created));
        Assert.False(store.TryCreateBooking(booking with { BookingId = "b2" }, out var replay));
        Assert.Equal(created.BookingId, replay.BookingId);
        store.AppendAudit("b1", "p1", "Quoted", "{}", now); store.AppendAudit("b1", "p1", "Authorised", "{}", now.AddSeconds(1));
        Assert.Equal("b1", store.FindByIdempotencyKey("stable-key")?.BookingId); var events = store.Audit("b1"); Assert.Equal(2, events.Count); Assert.Equal(events[0].CurrentHash, events[1].PreviousHash);
        Assert.All(events, item => Assert.Equal(
            HotelBookingAuditHash.Compute(item.PreviousHash, item.BookingId, item.PrincipalId, item.EventType, item.DataJson, item.Timestamp),
            item.CurrentHash));
    }

    [Fact]
    public void ServiceAuthorisation_MalformedSignature_IsRejectedWithoutThrowing()
    {
        var service = new HmacServiceActionAuthorisationService(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var proposal = new ServiceActionProposal("p", "principal", "agent", "provider", "book", "objective", JsonSerializer.SerializeToElement(new { }), 10, "GBP", "key");
        var quote = new ServiceQuote("q", "provider", "book", 10, "GBP", DateTimeOffset.UtcNow.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
        var authorisation = service.Issue(proposal, quote, "v1", DateTimeOffset.UtcNow) with { Signature = "not-hex" };

        Assert.False(service.Verify(authorisation, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ServiceOrchestrator_ReleasesProviderHold_WhenPaymentGateFails()
    {
        var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        var authorisations = new HmacServiceActionAuthorisationService(key);
        var provider = new RecordingServiceConnector();
        var proposal = new ServiceActionProposal("p", "principal", "agent", provider.ProviderId, "book", "objective", JsonSerializer.SerializeToElement(new { }), 10, "GBP", "key");
        var quote = new ServiceQuote("q", provider.ProviderId, "book", 10, "GBP", DateTimeOffset.UtcNow.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
        var policy = new BoundedServiceActionTrustPolicy(new HashSet<string>([provider.ProviderId]), new HashSet<string>(["book"]));

        var result = await new TrustedServiceActionOrchestrator(policy, authorisations).ExecuteAsync(
            proposal,
            quote,
            provider,
            DateTimeOffset.UtcNow,
            beforeExecution: (_, _, _, _, _) => Task.FromResult<ServiceExecutionResult?>(
                new(false, "Failed", null, JsonSerializer.SerializeToElement(new { }), "PAYMENT_FAILED")));

        Assert.False(result.Execution?.Succeeded);
        Assert.Equal(1, provider.ReleaseCount);
        Assert.Equal(0, provider.BookCount);
    }

    [Fact]
    public async Task HotelService_OverBudgetQuote_IsDeniedBeforeProviderExecution()
    {
        var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray(); var authorisations = new HmacServiceActionAuthorisationService(key); IServiceConnector hotel = new DemoHotelConnector(authorisations);
        var plan = await new ServicePlanningRouter(new ServiceConnectorRegistry([hotel]), [new HotelDomainCapability()]).PlanAsync(new("principal-1", "agent-1", "Book an accessible hotel room in London from 2026-12-10 to 2026-12-11 for 2 guests under £50", "hotel-demo"));
        var result = await new TrustedServiceActionOrchestrator(new BoundedServiceActionTrustPolicy(new HashSet<string>(["hotel-demo"]), new HashSet<string>(["book"])), authorisations).ExecuteAsync(plan.Proposal, plan.Quote, hotel, DateTimeOffset.UtcNow);
        Assert.False(result.TrustDecision.Approved); Assert.Contains("AMOUNT_EXCEEDS_LIMIT", result.TrustDecision.Reasons); Assert.Null(result.Authorisation); Assert.Null(result.Execution);
    }

    [Fact]
    public void MerchantRegistry_ResolvesByStableMerchantIdAndRejectsDuplicates()
    {
        var connector = Connector(); var registry = new MerchantConnectorRegistry([connector]);
        Assert.Same(connector, registry.GetRequired("grocerydemo"));
        Assert.Throws<InvalidOperationException>(() => new MerchantConnectorRegistry([connector, Connector()]));
    }

    private static DemoGroceryConnector Connector() => new(
        new HmacPurchaseAuthorisationService(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()),
        new MockPlatformPaymentProcessor());

    private sealed class StubProviderCapability(string providerId) : IProviderPlanningCapability
    {
        public bool CanHandle(ConsumerActionPlanningContext context) => context.ProviderId == providerId;
        public ProviderPlanningSession Open(ConsumerActionPlanningContext context) => new(providerId, "Hotel X", new HashSet<string>(["search_rooms", "get_quote"]), new object());
    }
    private sealed class StubDomainCapability : IDomainPlanningCapability
    {
        public ConsumerActionPlanningContext? Received { get; private set; }
        public string DomainId => "hotel";
        public bool CanHandle(ConsumerActionPlanningContext context, ProviderPlanningSession provider) => provider.Capabilities.Contains("search_rooms");
        public Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context, ProviderPlanningSession provider, CancellationToken cancellationToken)
        { Received = context; return Task.FromResult(new ConsumerPurchasePlan(PurchasePlanningStatus.Ready, context.Instruction, "Provider quote ready", 100, "EUR", [], [], 90, ["get_quote"])); }
    }

    private sealed class RecordingServiceConnector : IServiceConnector
    {
        public string ProviderId => "recording-hotel";
        public string ProviderName => "Recording Hotel";
        public int ReleaseCount { get; private set; }
        public int BookCount { get; private set; }
        public IReadOnlyCollection<CapabilityDescriptor> Capabilities { get; } = [];

        public Task<CapabilityInvocationResult> InvokeAsync(string capability, JsonElement input, CancellationToken cancellationToken = default)
        {
            if (capability == "reserve")
                return Task.FromResult(new CapabilityInvocationResult(true, JsonSerializer.SerializeToElement(new { reservationId = "hold-1" })));
            if (capability == "release")
            {
                ReleaseCount++;
                return Task.FromResult(new CapabilityInvocationResult(true, JsonSerializer.SerializeToElement(new { status = "released" })));
            }

            BookCount++;
            return Task.FromResult(new CapabilityInvocationResult(true, JsonSerializer.SerializeToElement(new { providerReference = "booking-1" })));
        }
    }
}
