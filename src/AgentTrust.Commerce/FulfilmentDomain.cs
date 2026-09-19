using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentTrust.Commerce;

public enum FulfilmentMode { MerchantDelivery, CustomerPickup, ThirdPartyDelivery }
public enum FulfilmentStatus { Pending, Accepted, Preparing, ReadyForPickup, CourierRequested, CourierAssigned, CourierArriving, Collected, OutForDelivery, Delivered, CollectedByCustomer, Cancelled, Failed, Unknown }

public sealed record FulfilmentOption(string FulfilmentOptionId, string ProviderId, FulfilmentMode Mode,
    string DisplayName, bool Available, string Currency, decimal EstimatedFee,
    DateTimeOffset? EstimatedReadyAt = null, DateTimeOffset? EstimatedPickupAt = null,
    DateTimeOffset? EstimatedDeliveryAt = null, string? ServiceAreaId = null,
    decimal? MinOrderAmount = null, decimal? MaxDistance = null, string? ProviderReference = null);
public sealed record FulfilmentQuoteRequest(string ProviderId, string OrderIntentId, FulfilmentMode Mode,
    string? DestinationReference = null, string? PickupLocationId = null, DateTimeOffset? RequestedTime = null);
public sealed record FulfilmentQuote(string QuoteId, string ProviderId, FulfilmentMode Mode, string Currency,
    decimal DeliveryFee, decimal ServiceFee, decimal Tax, decimal Discount, decimal Total,
    DateTimeOffset? EstimatedPickupAt, DateTimeOffset? EstimatedDeliveryAt, DateTimeOffset ExpiresAt,
    string ProviderReference);
public sealed record FulfilmentIntent(string FulfilmentIntentId, string PrincipalId, string AgentId,
    string ProviderId, string MerchantOrderId, FulfilmentMode Mode, string QuoteId, string QuoteHash,
    string? DestinationReference, string? PickupLocationId, string Currency, decimal TotalAmount,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record FulfilmentExecutionResult(string FulfilmentId, FulfilmentStatus Status,
    string ProviderReference, string? CourierReference = null, DateTimeOffset? EstimatedPickupAt = null,
    DateTimeOffset? EstimatedDeliveryAt = null, string? FailureReason = null);
public sealed record FulfilmentCancellationResult(string FulfilmentId, bool Cancelled,
    decimal CancellationFee, decimal? RefundAmount, string Currency, string ProviderReference,
    string? FailureReason = null);
public sealed record FulfilmentStatusEvent(string ProviderEventId, string FulfilmentId,
    FulfilmentStatus Status, DateTimeOffset ProviderTimestamp, string PayloadHash);

public interface IFulfilmentOptionsCapability
{
    Task<IReadOnlyList<FulfilmentOption>> GetFulfilmentOptionsAsync(string orderIntentId, CancellationToken ct = default);
}
public interface IFulfilmentQuoteCapability
{
    Task<FulfilmentQuote> GetFulfilmentQuoteAsync(FulfilmentQuoteRequest request, CancellationToken ct = default);
}
public interface IFulfilmentExecutionCapability
{
    Task<FulfilmentExecutionResult> ExecuteFulfilmentAsync(FulfilmentIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<FulfilmentCancellationResult> CancelFulfilmentAsync(string fulfilmentId, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<FulfilmentExecutionResult?> GetFulfilmentStatusAsync(string fulfilmentId, CancellationToken ct = default);
}
public interface IThirdPartyDeliveryConnector : IFulfilmentOptionsCapability, IFulfilmentQuoteCapability, IFulfilmentExecutionCapability
{
    string ProviderId { get; }
}
public interface IFulfilmentWebhookHandler
{
    bool HandleFulfilmentEvent(FulfilmentStatusEvent statusEvent);
}
public interface IFulfilmentReconciliationService
{
    Task<FulfilmentExecutionResult?> ReconcileAsync(string fulfilmentIntentId, CancellationToken ct = default);
}
public interface IFulfilmentStore
{
    void SaveQuote(FulfilmentQuote quote);
    void SaveIntent(FulfilmentIntent intent);
    void SaveExecution(FulfilmentExecutionResult execution, string fulfilmentIntentId);
    FulfilmentExecutionResult? FindExecution(string fulfilmentIntentId);
    IReadOnlyList<FulfilmentStatusEvent> History(string fulfilmentId);
    bool AppendStatus(FulfilmentStatusEvent statusEvent);
    FulfilmentIntent? FindIntent(string fulfilmentIntentId);
    IReadOnlyList<FulfilmentIntent> FindReconciliationCandidates(DateTimeOffset now, int limit);
    void MarkUnknown(string fulfilmentIntentId, string reason, DateTimeOffset nextAttemptAt);
    bool IsOwnedByProvider(string fulfilmentId, string providerId);
}

public static class FulfilmentStateMachine
{
    private static readonly IReadOnlyDictionary<FulfilmentStatus, IReadOnlySet<FulfilmentStatus>> Allowed =
        new Dictionary<FulfilmentStatus, IReadOnlySet<FulfilmentStatus>>
        {
            [FulfilmentStatus.Pending] = Set(FulfilmentStatus.Accepted, FulfilmentStatus.CourierRequested, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.Accepted] = Set(FulfilmentStatus.Preparing, FulfilmentStatus.ReadyForPickup, FulfilmentStatus.CourierRequested, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.Preparing] = Set(FulfilmentStatus.ReadyForPickup, FulfilmentStatus.CourierRequested, FulfilmentStatus.OutForDelivery, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.ReadyForPickup] = Set(FulfilmentStatus.CollectedByCustomer, FulfilmentStatus.CourierAssigned, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.CourierRequested] = Set(FulfilmentStatus.CourierAssigned, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.CourierAssigned] = Set(FulfilmentStatus.CourierArriving, FulfilmentStatus.Collected, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.CourierArriving] = Set(FulfilmentStatus.Collected, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.Collected] = Set(FulfilmentStatus.OutForDelivery, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.OutForDelivery] = Set(FulfilmentStatus.Delivered, FulfilmentStatus.Failed, FulfilmentStatus.Unknown),
            [FulfilmentStatus.Unknown] = Set(FulfilmentStatus.Accepted, FulfilmentStatus.Preparing, FulfilmentStatus.ReadyForPickup, FulfilmentStatus.CourierRequested, FulfilmentStatus.CourierAssigned, FulfilmentStatus.CourierArriving, FulfilmentStatus.Collected, FulfilmentStatus.OutForDelivery, FulfilmentStatus.Delivered, FulfilmentStatus.CollectedByCustomer, FulfilmentStatus.Cancelled, FulfilmentStatus.Failed)
        };

    public static bool CanTransition(FulfilmentStatus current, FulfilmentStatus next) =>
        current == next || Allowed.TryGetValue(current, out var transitions) && transitions.Contains(next);

    public static void EnsureTransition(FulfilmentStatus current, FulfilmentStatus next)
    {
        if (!CanTransition(current, next))
            throw new InvalidOperationException($"INVALID_STATE_TRANSITION:{current}->{next}");
    }

    private static IReadOnlySet<FulfilmentStatus> Set(params FulfilmentStatus[] values) => values.ToHashSet();
}

public static class FulfilmentQuoteBinding
{
    public static string Hash(FulfilmentQuote quote, FulfilmentQuoteRequest request)
    {
        var value = string.Join('|', quote.ProviderId, request.OrderIntentId, quote.Mode,
            request.DestinationReference, request.PickupLocationId, quote.Total,
            quote.Currency, quote.QuoteId, quote.ExpiresAt.ToUnixTimeMilliseconds());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed class InMemoryFulfilmentStore : IFulfilmentStore, IFulfilmentWebhookHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FulfilmentQuote> _quotes = [];
    private readonly Dictionary<string, FulfilmentIntent> _intents = [];
    private readonly Dictionary<string, FulfilmentExecutionResult> _executions = [];
    private readonly List<FulfilmentStatusEvent> _history = [];
    private readonly HashSet<string> _providerEvents = [];
    public void SaveQuote(FulfilmentQuote quote) { lock (_gate) _quotes[quote.QuoteId] = quote; }
    public void SaveIntent(FulfilmentIntent intent) { lock (_gate) _intents[intent.FulfilmentIntentId] = intent; }
    public void SaveExecution(FulfilmentExecutionResult execution, string fulfilmentIntentId) { lock (_gate) _executions[fulfilmentIntentId] = execution; }
    public FulfilmentExecutionResult? FindExecution(string fulfilmentIntentId) { lock (_gate) return _executions.GetValueOrDefault(fulfilmentIntentId); }
    public IReadOnlyList<FulfilmentStatusEvent> History(string fulfilmentId) { lock (_gate) return _history.Where(x => x.FulfilmentId == fulfilmentId).ToArray(); }
    public bool AppendStatus(FulfilmentStatusEvent statusEvent)
    {
        lock (_gate)
        {
            if (!_providerEvents.Add(statusEvent.ProviderEventId)) return false;
            _history.Add(statusEvent);
            return true;
        }
    }
    public FulfilmentIntent? FindIntent(string fulfilmentIntentId) { lock (_gate) return _intents.GetValueOrDefault(fulfilmentIntentId); }
    public IReadOnlyList<FulfilmentIntent> FindReconciliationCandidates(DateTimeOffset now, int limit)
    {
        lock (_gate)
            return _intents.Values.Where(x => !_executions.TryGetValue(x.FulfilmentIntentId, out var e) || e.Status == FulfilmentStatus.Unknown).Take(limit).ToArray();
    }
    public void MarkUnknown(string fulfilmentIntentId, string reason, DateTimeOffset nextAttemptAt)
    {
        lock (_gate)
            _executions[fulfilmentIntentId] = new($"unknown_{fulfilmentIntentId}", FulfilmentStatus.Unknown, "", FailureReason: reason);
    }
    public bool IsOwnedByProvider(string fulfilmentId, string providerId)
    {
        lock (_gate)
        {
            var intentId = _executions.FirstOrDefault(x => x.Value.FulfilmentId == fulfilmentId).Key;
            return intentId is not null && _intents.TryGetValue(intentId, out var intent)
                && string.Equals(intent.ProviderId, providerId, StringComparison.OrdinalIgnoreCase);
        }
    }
    public bool HandleFulfilmentEvent(FulfilmentStatusEvent statusEvent) => AppendStatus(statusEvent);
}
