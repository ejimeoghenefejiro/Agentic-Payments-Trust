using System.Text.Json;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Tests;

public sealed class FulfilmentTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();

    [Fact]
    public async Task Grocery_DeclaresAllThreeModesAndOwnsMerchantFee()
    {
        var connector = Grocery();
        var options = await connector.GetFulfilmentOptionsAsync("order-1");
        var quote = await connector.GetFulfilmentQuoteAsync(new(connector.MerchantId, "order-1", FulfilmentMode.MerchantDelivery, "saved-home"));
        Assert.Equal(Enum.GetValues<FulfilmentMode>().Order(), options.Select(x => x.Mode).Order());
        Assert.Equal(2.50m, quote.DeliveryFee);
        Assert.Equal(quote.DeliveryFee + quote.ServiceFee + quote.Tax - quote.Discount, quote.Total);
    }

    [Fact]
    public async Task Restaurant_PickupUsesProviderQuoteAndCreatesNoCourier()
    {
        var connector = new DemoRestaurantConnector(new HmacServiceActionAuthorisationService(Key));
        var quote = await connector.GetFulfilmentQuoteAsync(new(connector.ProviderId, "order-1", FulfilmentMode.CustomerPickup, PickupLocationId: "restaurant-main"));
        Assert.Equal(FulfilmentMode.CustomerPickup, quote.Mode);
        Assert.Equal(0m, quote.DeliveryFee);
        Assert.Equal(.50m, quote.ServiceFee);
        Assert.NotEqual("courier-demo", quote.ProviderId);
    }

    [Fact]
    public async Task ThirdPartyDelivery_IsTrustGatedAndIdempotent()
    {
        var auth = new HmacServiceActionAuthorisationService(Key); var store = new InMemoryFulfilmentStore(); var courier = new DemoThirdPartyDeliveryConnector(auth, store);
        var request = new FulfilmentQuoteRequest(courier.ProviderId, "merchant-order-1", FulfilmentMode.ThirdPartyDelivery, "saved-home");
        var fulfilmentQuote = await courier.GetFulfilmentQuoteAsync(request);
        var configuration = JsonSerializer.SerializeToElement(new { merchantOrderId = request.OrderIntentId, destinationReference = request.DestinationReference });
        var proposal = new ServiceActionProposal("proposal", "principal", "agent", courier.ProviderId, "execute_fulfilment", "deliver", configuration, 6m, "GBP", "stable-fulfilment-intent");
        var quote = new ServiceQuote(fulfilmentQuote.QuoteId, courier.ProviderId, "execute_fulfilment", fulfilmentQuote.Total, fulfilmentQuote.Currency, fulfilmentQuote.ExpiresAt, JsonSerializer.SerializeToElement(fulfilmentQuote));
        var orchestrator = new TrustedServiceActionOrchestrator(new BoundedServiceActionTrustPolicy(new HashSet<string>([courier.ProviderId]), new HashSet<string>(["execute_fulfilment"])), auth);
        var first = await orchestrator.ExecuteAsync(proposal, quote, courier, DateTimeOffset.UtcNow);
        var replay = await orchestrator.ExecuteAsync(proposal, quote, courier, DateTimeOffset.UtcNow);
        Assert.True(first.Execution?.Succeeded); Assert.Equal(first.Execution?.ProviderReference, replay.Execution?.ProviderReference); Assert.Equal(1, courier.BookingCount);
        Assert.NotNull(await courier.ReconcileAsync(proposal.IdempotencyKey));
    }

    [Fact]
    public async Task ThirdPartyDelivery_OverBudgetCannotBookCourier()
    {
        var auth = new HmacServiceActionAuthorisationService(Key); var courier = new DemoThirdPartyDeliveryConnector(auth, new InMemoryFulfilmentStore());
        var fq = await courier.GetFulfilmentQuoteAsync(new(courier.ProviderId, "order", FulfilmentMode.ThirdPartyDelivery, "saved-home"));
        var proposal = new ServiceActionProposal("p", "principal", "agent", courier.ProviderId, "execute_fulfilment", "deliver", JsonSerializer.SerializeToElement(new { merchantOrderId = "order", destinationReference = "saved-home" }), 4m, "GBP", "key");
        var quote = new ServiceQuote(fq.QuoteId, courier.ProviderId, "execute_fulfilment", fq.Total, fq.Currency, fq.ExpiresAt, JsonSerializer.SerializeToElement(fq));
        var result = await new TrustedServiceActionOrchestrator(new BoundedServiceActionTrustPolicy(new HashSet<string>([courier.ProviderId]), new HashSet<string>(["execute_fulfilment"])), auth).ExecuteAsync(proposal, quote, courier, DateTimeOffset.UtcNow);
        Assert.False(result.TrustDecision.Approved); Assert.Equal(0, courier.BookingCount);
    }

    [Fact]
    public void FulfilmentWebhook_IsIdempotentAndKeepsSeparateStatusHistory()
    {
        var store = new InMemoryFulfilmentStore(); var evt = new FulfilmentStatusEvent("event-1", "fulfilment-1", FulfilmentStatus.OutForDelivery, DateTimeOffset.UtcNow, "hash");
        Assert.True(store.HandleFulfilmentEvent(evt)); Assert.False(store.HandleFulfilmentEvent(evt));
        Assert.Single(store.History("fulfilment-1"));
        Assert.NotEqual(typeof(FulfilmentStatus), typeof(RestaurantOrderStatus));
    }

    [Fact]
    public void QuoteHashChangesWhenMaterialDeliveryTermsChange()
    {
        var quote = new FulfilmentQuote("q", "courier", FulfilmentMode.ThirdPartyDelivery, "GBP", 5, 0, 0, 0, 5, null, null, DateTimeOffset.UtcNow.AddMinutes(2), "ref");
        var first = FulfilmentQuoteBinding.Hash(quote, new("courier", "order", FulfilmentMode.ThirdPartyDelivery, "address-1"));
        var changed = FulfilmentQuoteBinding.Hash(quote with { Total = 6, DeliveryFee = 6 }, new("courier", "order", FulfilmentMode.ThirdPartyDelivery, "address-2"));
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void GenericAgentHasNoCourierOrFulfilmentExecutionDependency()
    {
        var dependencies = typeof(AgentTrust.Api.ConsumerPurchaseRequestAgent).GetConstructors().SelectMany(x => x.GetParameters()).Select(x => x.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(DemoThirdPartyDeliveryConnector), dependencies);
        Assert.DoesNotContain(typeof(IFulfilmentExecutionCapability), dependencies);
    }

    [Fact]
    public void StateMachine_RejectsBackwardProviderTransition()
    {
        Assert.True(FulfilmentStateMachine.CanTransition(FulfilmentStatus.OutForDelivery, FulfilmentStatus.Delivered));
        var error = Assert.Throws<InvalidOperationException>(() =>
            FulfilmentStateMachine.EnsureTransition(FulfilmentStatus.Delivered, FulfilmentStatus.Preparing));
        Assert.StartsWith("INVALID_STATE_TRANSITION", error.Message);
    }

    [Fact]
    public void DurableStore_IdempotencySurvivesNewDbContext()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(connection).Options;
        var intent = new FulfilmentIntent("stable-key", "principal", "agent", "courier", "order", FulfilmentMode.ThirdPartyDelivery,
            "quote", "hash", "address", null, "GBP", 5m, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2));
        var execution = new FulfilmentExecutionResult("fulfilment", FulfilmentStatus.CourierAssigned, "provider-reference");
        using (var first = new AgentTrustDbContext(options))
        {
            first.Database.EnsureCreated();
            var store = new EfFulfilmentStore(first);
            store.SaveIntent(intent); store.SaveExecution(execution, intent.FulfilmentIntentId);
        }
        using (var restarted = new AgentTrustDbContext(options))
        {
            var store = new EfFulfilmentStore(restarted);
            store.SaveIntent(intent); store.SaveExecution(execution, intent.FulfilmentIntentId);
            Assert.Equal("provider-reference", store.FindExecution(intent.FulfilmentIntentId)?.ProviderReference);
        }
    }

    [Fact]
    public void DurableStore_RejectsChangedPayloadForSameIdempotencyKey()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        var options = new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(connection).Options;
        using var db = new AgentTrustDbContext(options); db.Database.EnsureCreated();
        var store = new EfFulfilmentStore(db);
        var now = DateTimeOffset.UtcNow;
        store.SaveIntent(new("stable-key", "principal", "agent", "courier", "order", FulfilmentMode.ThirdPartyDelivery, "q", "hash-a", "address", null, "GBP", 5m, now, now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => store.SaveIntent(new("stable-key", "principal", "agent", "courier", "order", FulfilmentMode.ThirdPartyDelivery, "q", "hash-b", "address", null, "GBP", 6m, now, now.AddMinutes(2))));
    }

    [Fact]
    public async Task ExecutionKillSwitch_BlocksProviderBeforeReservationOrExecution()
    {
        var auth = new HmacServiceActionAuthorisationService(Key);
        var courier = new DemoThirdPartyDeliveryConnector(auth, new InMemoryFulfilmentStore());
        var fq = await courier.GetFulfilmentQuoteAsync(new(courier.ProviderId, "order", FulfilmentMode.ThirdPartyDelivery, "saved-home"));
        var proposal = new ServiceActionProposal("p", "principal", "agent", courier.ProviderId, "execute_fulfilment", "deliver", JsonSerializer.SerializeToElement(new { merchantOrderId = "order", destinationReference = "saved-home" }), 6m, "GBP", "key");
        var quote = new ServiceQuote(fq.QuoteId, courier.ProviderId, proposal.Action, fq.Total, fq.Currency, fq.ExpiresAt, JsonSerializer.SerializeToElement(fq));
        var control = new DenyExecution();
        var result = await new TrustedServiceActionOrchestrator(new BoundedServiceActionTrustPolicy(new HashSet<string>([courier.ProviderId]), new HashSet<string>([proposal.Action])), auth, control).ExecuteAsync(proposal, quote, courier, DateTimeOffset.UtcNow);
        Assert.False(result.TrustDecision.Approved);
        Assert.Contains("EXTERNAL_EXECUTION_DISABLED", result.TrustDecision.Reasons);
        Assert.Equal(0, courier.BookingCount);
    }

    [Fact]
    public void DurableStore_EnforcesProviderOwnershipForWebhookTarget()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        var options = new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(connection).Options;
        using var db = new AgentTrustDbContext(options); db.Database.EnsureCreated();
        var store = new EfFulfilmentStore(db); var now = DateTimeOffset.UtcNow;
        store.SaveIntent(new("intent", "principal", "agent", "provider-a", "order", FulfilmentMode.MerchantDelivery, "q", "hash", "address", null, "GBP", 2m, now, now.AddMinutes(2)));
        store.SaveExecution(new("fulfilment", FulfilmentStatus.Accepted, "provider-ref"), "intent");
        Assert.True(store.IsOwnedByProvider("fulfilment", "provider-a"));
        Assert.False(store.IsOwnedByProvider("fulfilment", "provider-b"));
    }

    private sealed class DenyExecution : IExternalExecutionControl
    {
        public bool IsAllowed(ServiceActionProposal proposal, out string reason) { reason = "EXTERNAL_EXECUTION_DISABLED"; return false; }
    }

    private static DemoGroceryConnector Grocery() => new(new HmacPurchaseAuthorisationService(Key), new MockPlatformPaymentProcessor());
}
