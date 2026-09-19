using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AgentTrust.Commerce;

namespace AgentTrust.Connectors;

public sealed class DemoMerchantFulfilmentConnector(string providerId, IServiceActionAuthorisationService authorisations)
    : IFulfilmentOptionsCapability, IFulfilmentQuoteCapability, IFulfilmentExecutionCapability
{
    private readonly ConcurrentDictionary<string, FulfilmentQuote> _quotes = [];
    private readonly ConcurrentDictionary<string, FulfilmentExecutionResult> _executions = [];
    public int ExecutionCount { get; private set; }
    public Task<IReadOnlyList<FulfilmentOption>> GetFulfilmentOptionsAsync(string orderIntentId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult<IReadOnlyList<FulfilmentOption>>([
            new("merchant-delivery", providerId, FulfilmentMode.MerchantDelivery, "Merchant delivery", true, "GBP", 2.50m, EstimatedDeliveryAt: now.AddMinutes(35), ServiceAreaId: "default-area"),
            new("customer-pickup", providerId, FulfilmentMode.CustomerPickup, "Customer pickup", true, "GBP", 0.50m, EstimatedReadyAt: now.AddMinutes(15), ProviderReference: "pickup-main")]);
    }
    public Task<FulfilmentQuote> GetFulfilmentQuoteAsync(FulfilmentQuoteRequest request, CancellationToken ct = default)
    {
        if (request.ProviderId != providerId || request.Mode == FulfilmentMode.ThirdPartyDelivery) throw new InvalidOperationException("FULFILMENT_MODE_NOT_SUPPORTED");
        if (request.Mode == FulfilmentMode.MerchantDelivery && string.IsNullOrWhiteSpace(request.DestinationReference)) throw new InvalidOperationException("DESTINATION_REQUIRED");
        if (request.Mode == FulfilmentMode.CustomerPickup && string.IsNullOrWhiteSpace(request.PickupLocationId)) throw new InvalidOperationException("PICKUP_LOCATION_REQUIRED");
        var delivery = request.Mode == FulfilmentMode.MerchantDelivery ? 2.50m : 0m;
        var service = request.Mode == FulfilmentMode.CustomerPickup ? .50m : 0m;
        var quote = new FulfilmentQuote($"fulfilment_quote_{Guid.NewGuid():N}", providerId, request.Mode, "GBP", delivery, service, 0, 0, delivery + service,
            request.Mode == FulfilmentMode.MerchantDelivery ? DateTimeOffset.UtcNow.AddMinutes(20) : null,
            request.Mode == FulfilmentMode.MerchantDelivery ? DateTimeOffset.UtcNow.AddMinutes(35) : null,
            DateTimeOffset.UtcNow.AddMinutes(5), $"merchant_fulfilment_{Guid.NewGuid():N}");
        _quotes[quote.QuoteId] = quote; return Task.FromResult(quote);
    }
    public Task<FulfilmentExecutionResult> ExecuteFulfilmentAsync(FulfilmentIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    {
        Validate(intent, authorisation, "execute_fulfilment");
        var result = _executions.GetOrAdd(intent.FulfilmentIntentId, _ => { ExecutionCount++; return new($"fulfilment_{Guid.NewGuid():N}", intent.Mode == FulfilmentMode.CustomerPickup ? FulfilmentStatus.ReadyForPickup : FulfilmentStatus.Preparing, $"merchant_ref_{Guid.NewGuid():N}"); });
        return Task.FromResult(result);
    }
    public Task<FulfilmentCancellationResult> CancelFulfilmentAsync(string fulfilmentId, ServiceActionAuthorisation authorisation, CancellationToken ct = default) => Task.FromResult(new FulfilmentCancellationResult(fulfilmentId, true, 0, null, "GBP", $"cancel_{fulfilmentId}"));
    public Task<FulfilmentExecutionResult?> GetFulfilmentStatusAsync(string fulfilmentId, CancellationToken ct = default) => Task.FromResult(_executions.Values.FirstOrDefault(x => x.FulfilmentId == fulfilmentId));
    private void Validate(FulfilmentIntent intent, ServiceActionAuthorisation auth, string action) { if (!authorisations.Verify(auth, DateTimeOffset.UtcNow) || auth.ProviderId != providerId || auth.Action != action || auth.QuoteId != intent.QuoteId || auth.Amount != intent.TotalAmount || intent.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("AUTHORISATION_INVALID"); }
}

public sealed class DemoThirdPartyDeliveryConnector(IServiceActionAuthorisationService authorisations, IFulfilmentStore store)
    : IServiceConnector, IThirdPartyDeliveryConnector, IFulfilmentReconciliationService
{
    private readonly ConcurrentDictionary<string, FulfilmentQuote> _quotes = [];
    private readonly ConcurrentDictionary<string, FulfilmentExecutionResult> _bookings = [];
    public string ProviderId => "courier-demo";
    public string ProviderName => "Courier Demo";
    public int BookingCount { get; private set; }
    public IReadOnlyCollection<CapabilityDescriptor> Capabilities { get; } = [
        new("get_fulfilment_options", CapabilityFamily.Discovery, "order", "options"),
        new("get_fulfilment_quote", CapabilityFamily.Quote, "order,destination", "quote"),
        new("reserve", CapabilityFamily.Reservation, "quoteId", "hold"), new("release", CapabilityFamily.Reservation, "hold", "status"),
        new("execute_fulfilment", CapabilityFamily.Execution, "intent,authorisation", "fulfilment"),
        new("cancel_fulfilment", CapabilityFamily.Lifecycle, "fulfilment,authorisation", "cancellation"),
        new("get_fulfilment_status", CapabilityFamily.Lifecycle, "fulfilment", "status")];
    public Task<IReadOnlyList<FulfilmentOption>> GetFulfilmentOptionsAsync(string orderIntentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FulfilmentOption>>([
        new("courier-value", ProviderId, FulfilmentMode.ThirdPartyDelivery, "Value courier", true, "GBP", 4.50m, EstimatedDeliveryAt: DateTimeOffset.UtcNow.AddMinutes(45)),
        new("courier-fast", ProviderId, FulfilmentMode.ThirdPartyDelivery, "Fast courier", true, "GBP", 6.50m, EstimatedDeliveryAt: DateTimeOffset.UtcNow.AddMinutes(25))]);
    public Task<FulfilmentQuote> GetFulfilmentQuoteAsync(FulfilmentQuoteRequest request, CancellationToken ct = default)
    { if (request.ProviderId != ProviderId || request.Mode != FulfilmentMode.ThirdPartyDelivery || string.IsNullOrWhiteSpace(request.DestinationReference)) throw new InvalidOperationException("INVALID_COURIER_REQUEST"); var quote = new FulfilmentQuote($"courier_quote_{Guid.NewGuid():N}", ProviderId, request.Mode, "GBP", 4.50m, .50m, 0, 0, 5m, DateTimeOffset.UtcNow.AddMinutes(20), DateTimeOffset.UtcNow.AddMinutes(45), DateTimeOffset.UtcNow.AddMinutes(2), $"courier_quote_ref_{Guid.NewGuid():N}"); _quotes[quote.QuoteId] = quote; store.SaveQuote(quote); return Task.FromResult(quote); }
    public Task<FulfilmentExecutionResult> ExecuteFulfilmentAsync(FulfilmentIntent intent, ServiceActionAuthorisation auth, CancellationToken ct = default)
    {
        if (!authorisations.Verify(auth, DateTimeOffset.UtcNow) || auth.ProviderId != ProviderId || auth.Action != "execute_fulfilment" || auth.QuoteId != intent.QuoteId || auth.Amount != intent.TotalAmount || intent.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("AUTHORISATION_INVALID");
        var quote = _quotes.GetValueOrDefault(intent.QuoteId) ?? throw new InvalidOperationException("QUOTE_NOT_FOUND");
        var request = new FulfilmentQuoteRequest(ProviderId, intent.MerchantOrderId, intent.Mode, intent.DestinationReference, intent.PickupLocationId);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(intent.QuoteHash), Convert.FromHexString(FulfilmentQuoteBinding.Hash(quote, request))))
            throw new InvalidOperationException("QUOTE_BINDING_INVALID");
        store.SaveIntent(intent);
        if (store.FindExecution(intent.FulfilmentIntentId) is { Status: not FulfilmentStatus.Unknown } saved) return Task.FromResult(saved);
        try
        {
            var result = _bookings.GetOrAdd(intent.FulfilmentIntentId, _ => { BookingCount++; return new($"courier_fulfilment_{Guid.NewGuid():N}", FulfilmentStatus.CourierAssigned, $"courier_ref_{Guid.NewGuid():N}", $"driver_{Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddMinutes(20), DateTimeOffset.UtcNow.AddMinutes(45)); });
            store.SaveExecution(result, intent.FulfilmentIntentId);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            store.MarkUnknown(intent.FulfilmentIntentId, "PROVIDER_TIMEOUT", DateTimeOffset.UtcNow.AddMinutes(1));
            throw new InvalidOperationException("EXECUTION_UNKNOWN");
        }
        catch (HttpRequestException)
        {
            store.MarkUnknown(intent.FulfilmentIntentId, "PROVIDER_CONNECTION_LOST", DateTimeOffset.UtcNow.AddMinutes(1));
            throw new InvalidOperationException("EXECUTION_UNKNOWN");
        }
    }
    public Task<FulfilmentCancellationResult> CancelFulfilmentAsync(string id, ServiceActionAuthorisation auth, CancellationToken ct = default)
    { if (!authorisations.Verify(auth, DateTimeOffset.UtcNow) || auth.ProviderId != ProviderId || auth.Action != "cancel_fulfilment") throw new InvalidOperationException("AUTHORISATION_INVALID"); return Task.FromResult(new FulfilmentCancellationResult(id, true, 2m, null, "GBP", $"cancel_{id}")); }
    public Task<FulfilmentExecutionResult?> GetFulfilmentStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(_bookings.Values.FirstOrDefault(x => x.FulfilmentId == id));
    public Task<FulfilmentExecutionResult?> ReconcileAsync(string intentId, CancellationToken ct = default) => Task.FromResult(_bookings.GetValueOrDefault(intentId) ?? store.FindExecution(intentId));
    public Task<CapabilityInvocationResult> InvokeAsync(string capability, JsonElement input, CancellationToken ct = default)
    {
        try
        {
            var result = capability switch
            {
                "reserve" => new CapabilityInvocationResult(true, JsonSerializer.SerializeToElement(new { reservationId = $"courier_hold_{Guid.NewGuid():N}" })),
                "release" => new CapabilityInvocationResult(true, JsonSerializer.SerializeToElement(new { status = "released" })),
                "execute_fulfilment" => Execute(input),
                "get_fulfilment_status" => Status(input),
                _ => new CapabilityInvocationResult(false, JsonSerializer.SerializeToElement(new { error = "CAPABILITY_NOT_SUPPORTED" }), "CAPABILITY_NOT_SUPPORTED")
            };
            return Task.FromResult(result);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new CapabilityInvocationResult(false, JsonSerializer.SerializeToElement(new { error = ex.Message }), ex.Message));
        }
    }
    private CapabilityInvocationResult Execute(JsonElement input) { var p = input.GetProperty("proposal").Deserialize<ServiceActionProposal>()!; var q = input.GetProperty("quote").Deserialize<ServiceQuote>()!; var auth = input.GetProperty("authorisation").Deserialize<ServiceActionAuthorisation>()!; var fq = _quotes.GetValueOrDefault(q.QuoteId) ?? throw new InvalidOperationException("QUOTE_NOT_FOUND"); var request = new FulfilmentQuoteRequest(ProviderId, p.Configuration.GetProperty("merchantOrderId").GetString()!, FulfilmentMode.ThirdPartyDelivery, p.Configuration.GetProperty("destinationReference").GetString()); var intent = new FulfilmentIntent(p.IdempotencyKey, p.PrincipalId, p.AgentId, ProviderId, request.OrderIntentId, request.Mode, fq.QuoteId, FulfilmentQuoteBinding.Hash(fq, request), request.DestinationReference, null, fq.Currency, fq.Total, DateTimeOffset.UtcNow, fq.ExpiresAt); var result = ExecuteFulfilmentAsync(intent, auth).GetAwaiter().GetResult(); return new(true, JsonSerializer.SerializeToElement(new { providerReference = result.ProviderReference, result.FulfilmentId, result.Status })); }
    private CapabilityInvocationResult Status(JsonElement input) { var result = _bookings.Values.FirstOrDefault(x => x.FulfilmentId == input.GetProperty("fulfilmentId").GetString()); return result is null ? new(false, JsonSerializer.SerializeToElement(new { error = "NOT_FOUND" }), "NOT_FOUND") : new(true, JsonSerializer.SerializeToElement(result)); }
}
