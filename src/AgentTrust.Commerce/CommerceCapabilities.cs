using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentTrust.Commerce;

public interface IProductSearchCapability { Task<IReadOnlyList<Product>> SearchProductsAsync(string query, CancellationToken cancellationToken = default); Task<Product?> GetProductAsync(string productId, CancellationToken cancellationToken = default); }
public interface IBasketCapability { Task<Basket> CreateBasketAsync(string principalId, CancellationToken cancellationToken = default); Task<Basket> AddBasketItemAsync(string basketId, string productId, int quantity, bool substitutions, CancellationToken cancellationToken = default); Task<Basket> RemoveBasketItemAsync(string basketId, string productId, CancellationToken cancellationToken = default); Task<Basket> GetBasketAsync(string basketId, CancellationToken cancellationToken = default); }
public interface IQuoteCapability { Task<CommerceQuote> GetQuoteAsync(string basketId, string deliveryOptionId, CancellationToken cancellationToken = default); }
public interface IDeliveryCapability { Task<IReadOnlyList<DeliveryOption>> GetDeliveryOptionsAsync(string basketId, CancellationToken cancellationToken = default); Task SelectDeliveryOptionAsync(string basketId, string deliveryOptionId, CancellationToken cancellationToken = default); }
public interface ICheckoutCapability { Task PrepareCheckoutAsync(PurchaseIntent intent, CancellationToken cancellationToken = default); Task<ConnectorPurchaseResult> ExecutePurchaseAsync(PurchaseIntent intent, PurchaseAuthorisation authorisation, CancellationToken cancellationToken = default); }
public interface ICommerceConnector : IProductSearchCapability, IBasketCapability, IQuoteCapability, IDeliveryCapability, ICheckoutCapability { string MerchantId { get; } string MerchantName { get; } }

public static class CommerceCapabilityCatalog
{
    public static IReadOnlySet<string> Describe(object provider)
    {
        var capabilities=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(provider is IProductSearchCapability){capabilities.Add("search_products");capabilities.Add("get_product");}
        if(provider is IBasketCapability){capabilities.Add("create_basket");capabilities.Add("update_basket");}
        if(provider is IQuoteCapability)capabilities.Add("get_quote");
        if(provider is IDeliveryCapability)capabilities.Add("get_delivery_options");
        if(provider is ICheckoutCapability)capabilities.Add("execute_purchase");
        return capabilities;
    }
}

public sealed class MerchantConnectorRegistry
{
    private readonly IReadOnlyDictionary<string,ICommerceConnector> _connectors;
    public MerchantConnectorRegistry(IEnumerable<ICommerceConnector> connectors)
    {
        var materialized=connectors.ToArray();
        if(materialized.GroupBy(x=>x.MerchantId,StringComparer.OrdinalIgnoreCase).Any(x=>x.Count()>1))
            throw new InvalidOperationException("Merchant connector identifiers must be unique.");
        _connectors=materialized.ToDictionary(x=>x.MerchantId,StringComparer.OrdinalIgnoreCase);
    }
    public IReadOnlyList<ICommerceConnector> All=>_connectors.Values.ToArray();
    public bool TryGet(string merchantId,out ICommerceConnector connector)=>_connectors.TryGetValue(merchantId,out connector!);
    public ICommerceConnector GetRequired(string merchantId)=>_connectors.TryGetValue(merchantId,out var connector)
        ?connector:throw new KeyNotFoundException($"Merchant connector '{merchantId}' is not registered.");
}

public sealed record MerchantPlanningContext(string PrincipalId,string MerchantId,string MerchantName,string Currency,
    IReadOnlySet<string> Capabilities);
public sealed record ProposedMerchantItem(string Concept,int Quantity,bool AllowSubstitution=true);
public sealed record MerchantPlanningQuote(string QuoteId,string MerchantId,string MerchantName,string Currency,
    IReadOnlyList<BasketItem> Items,decimal Subtotal,decimal DeliveryFee,decimal ServiceFee,decimal Discount,
    decimal Tax,decimal Total,DateTimeOffset ExpiresAt,string DeliveryOptionId);
public sealed record ObjectiveExpansion(string Objective,IReadOnlyList<string> RequiredConcepts,IReadOnlyList<string> OptionalConcepts,string Provenance);
public sealed record ObjectiveClarification(string Summary,string Message,string Question,
    IReadOnlyList<string> SuggestedConcepts,string Provenance);
public interface IObjectiveExpansionCapability
{
    string CapabilityName { get; }
    bool CanExpand(string objective);
    Task<ObjectiveExpansion?> ExpandAsync(string objective,CancellationToken cancellationToken=default);
    Task<ObjectiveClarification?> ClarifyAsync(string objective,CancellationToken cancellationToken=default) =>
        Task.FromResult<ObjectiveClarification?>(null);
}
public interface IMerchantPlanningToolset
{
    MerchantPlanningContext Context { get; }
    Task<IReadOnlyList<Product>> SearchAsync(string concept,CancellationToken cancellationToken=default);
    Task<MerchantPlanningQuote> QuoteAsync(IReadOnlyList<ProposedMerchantItem> items,CancellationToken cancellationToken=default);
    Task<ObjectiveExpansion?> ExpandObjectiveAsync(string objective,CancellationToken cancellationToken=default);
}

public enum ConnectorPurchaseStatus { Succeeded, RequiresAction, Processing, Failed, Unknown }
public sealed record ConnectorPurchaseResult(ConnectorPurchaseStatus Status, string? ProviderReference,
    string? RequiredAction, string? FailureReason, PurchaseReceipt? Receipt);
public enum PlatformPaymentStatus { Succeeded, RequiresAction, Processing, Failed, Unknown }
public sealed record PlatformPaymentResult(PlatformPaymentStatus Status, string? ProviderReference,
    string? RequiredAction, string? FailureReason);
public enum PlatformRefundStatus { Succeeded, Processing, Failed, Unsupported }
public sealed record PlatformRefundResult(PlatformRefundStatus Status, string? ProviderReference, string? FailureReason);
public interface IPlatformPaymentProcessor
{
    string ProviderName { get; }
    Task<PlatformPaymentResult> ProcessAsync(PurchaseIntent intent, CancellationToken cancellationToken = default);
    Task<PlatformRefundResult> RefundAsync(
        string paymentReference,
        decimal amount,
        string currency,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PlatformRefundResult(
            PlatformRefundStatus.Unsupported,
            null,
            "REFUNDS_NOT_SUPPORTED"));
}

public sealed record LivePurchaseOptions(bool Enabled = false, decimal MaxPilotAmountGbp = 5,
    IReadOnlySet<string>? AllowedPrincipalIds = null, IReadOnlySet<string>? AllowedMerchantIds = null,
    bool RequireExplicitLiveConfirmation = true);
public sealed record LiveExecutionContext(bool IsLiveMode, bool ExplicitlyConfirmed);
public sealed class LivePurchaseGate
{
    private readonly LivePurchaseOptions _options;
    public LivePurchaseGate(LivePurchaseOptions options) => _options = options;
    public IReadOnlyList<string> Validate(PurchaseIntent intent, LiveExecutionContext context)
    {
        if (!context.IsLiveMode) return [];
        var failures = new List<string>();
        if (!_options.Enabled) failures.Add("LIVE_PURCHASE_DISABLED");
        if ((_options.AllowedPrincipalIds ?? new HashSet<string>()).Contains(intent.PrincipalId) == false) failures.Add("PRINCIPAL_NOT_ALLOWLISTED");
        if ((_options.AllowedMerchantIds ?? new HashSet<string>()).Contains(intent.MerchantId) == false) failures.Add("MERCHANT_NOT_ALLOWLISTED");
        if (!string.Equals(intent.Currency, "GBP", StringComparison.OrdinalIgnoreCase) || intent.TotalAmount > _options.MaxPilotAmountGbp) failures.Add("ABOVE_LIVE_PILOT_LIMIT");
        if (_options.RequireExplicitLiveConfirmation && !context.ExplicitlyConfirmed) failures.Add("LIVE_CONFIRMATION_REQUIRED");
        return failures;
    }
}
