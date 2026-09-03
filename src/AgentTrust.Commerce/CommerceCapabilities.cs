using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentTrust.Commerce;

public interface IProductSearchCapability { Task<IReadOnlyList<Product>> SearchProductsAsync(string query, CancellationToken cancellationToken = default); Task<Product?> GetProductAsync(string productId, CancellationToken cancellationToken = default); }
public interface IBasketCapability { Task<Basket> CreateBasketAsync(string principalId, CancellationToken cancellationToken = default); Task<Basket> AddBasketItemAsync(string basketId, string productId, int quantity, bool substitutions, CancellationToken cancellationToken = default); Task<Basket> RemoveBasketItemAsync(string basketId, string productId, CancellationToken cancellationToken = default); Task<Basket> GetBasketAsync(string basketId, CancellationToken cancellationToken = default); }
public interface IQuoteCapability { Task<CommerceQuote> GetQuoteAsync(string basketId, string deliveryOptionId, CancellationToken cancellationToken = default); }
public interface IDeliveryCapability { Task<IReadOnlyList<DeliveryOption>> GetDeliveryOptionsAsync(string basketId, CancellationToken cancellationToken = default); Task SelectDeliveryOptionAsync(string basketId, string deliveryOptionId, CancellationToken cancellationToken = default); }
public interface ICheckoutCapability { Task PrepareCheckoutAsync(PurchaseIntent intent, CancellationToken cancellationToken = default); Task<ConnectorPurchaseResult> ExecutePurchaseAsync(PurchaseIntent intent, PurchaseAuthorisation authorisation, CancellationToken cancellationToken = default); }
public interface ICommerceConnector : IProductSearchCapability, IBasketCapability, IQuoteCapability, IDeliveryCapability, ICheckoutCapability { string MerchantId { get; } string MerchantName { get; } }

public enum ConnectorPurchaseStatus { Succeeded, RequiresAction, Processing, Failed, Unknown }
public sealed record ConnectorPurchaseResult(ConnectorPurchaseStatus Status, string? ProviderReference,
    string? RequiredAction, string? FailureReason, PurchaseReceipt? Receipt);
public enum PlatformPaymentStatus { Succeeded, RequiresAction, Processing, Failed, Unknown }
public sealed record PlatformPaymentResult(PlatformPaymentStatus Status, string? ProviderReference,
    string? RequiredAction, string? FailureReason);
public interface IPlatformPaymentProcessor
{
    string ProviderName { get; }
    Task<PlatformPaymentResult> ProcessAsync(PurchaseIntent intent, CancellationToken cancellationToken = default);
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
