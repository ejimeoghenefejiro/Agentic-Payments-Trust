using AgentTrust.Commerce;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentTrust.Connectors;

/// <summary>Controlled first-party grocery provider. It has no agent/LLM dependency and refuses
/// checkout unless the exact intent carries a valid trusted authorisation.</summary>
public sealed class DemoGroceryConnector : ICommerceConnector
{
    private readonly object _gate = new(); private readonly IPurchaseAuthorisationService _authorisations;
    private readonly IPlatformPaymentProcessor _payments;
    private readonly ICommerceDurability _durability;
    private readonly Dictionary<string, Product> _catalogue; private readonly Dictionary<string, MutableBasket> _baskets = new();
    private readonly Dictionary<string, ConnectorPurchaseResult> _purchases = new();
    private sealed class MutableBasket { public required string Id; public required string PrincipalId; public Dictionary<string, (int Quantity, bool Substitute)> Items { get; } = new(); public string? DeliveryOptionId; }
    public string MerchantId => "GroceryDemo"; public string MerchantName => "Demo Grocery";

    public DemoGroceryConnector(IPurchaseAuthorisationService authorisations, IPlatformPaymentProcessor payments,
        IEnumerable<Product>? catalogue = null, ICommerceDurability? durability = null)
    {
        _authorisations = authorisations; _payments = payments; _durability = durability ?? new NullCommerceDurability();
        _catalogue = (catalogue is not null && catalogue.Any() ? catalogue : DefaultCatalogue()).ToDictionary(x => x.ProductId, StringComparer.OrdinalIgnoreCase);
    }
    public Task<IReadOnlyList<Product>> SearchProductsAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Product>>(_catalogue.Values.Where(p => p.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || p.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList());
    public Task<Product?> GetProductAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(_catalogue.GetValueOrDefault(id));
    public Task<Basket> CreateBasketAsync(string principal, CancellationToken cancellationToken = default)
    { lock (_gate) { var id = $"basket_{Guid.NewGuid():N}"; _baskets[id] = new MutableBasket { Id = id, PrincipalId = principal }; return Task.FromResult(Snapshot(_baskets[id])); } }
    public Task<Basket> AddBasketItemAsync(string basketId, string productId, int quantity, bool substitutions, CancellationToken cancellationToken = default)
    { if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity)); lock (_gate) { var b = RequireBasket(basketId); var p = RequireProduct(productId); if (p.AvailableQuantity < quantity) throw new InvalidOperationException("Insufficient stock."); b.Items[productId] = (quantity, substitutions); return Task.FromResult(Snapshot(b)); } }
    public Task<Basket> RemoveBasketItemAsync(string basketId, string productId, CancellationToken cancellationToken = default)
    { lock (_gate) { var b = RequireBasket(basketId); b.Items.Remove(productId); return Task.FromResult(Snapshot(b)); } }
    public Task<Basket> GetBasketAsync(string basketId, CancellationToken cancellationToken = default)
    { lock (_gate) return Task.FromResult(Snapshot(RequireBasket(basketId))); }
    public Task<IReadOnlyList<DeliveryOption>> GetDeliveryOptionsAsync(string basketId, CancellationToken cancellationToken = default)
    { lock (_gate) RequireBasket(basketId); var now = DateTimeOffset.UtcNow; return Task.FromResult<IReadOnlyList<DeliveryOption>>([
        new("standard", "Standard delivery", 2.50m, now.AddDays(1), now.AddDays(1).AddHours(2)),
        new("express", "Express delivery", 5m, now.AddHours(2), now.AddHours(3))]); }
    public Task SelectDeliveryOptionAsync(string basketId, string deliveryOptionId, CancellationToken cancellationToken = default)
    { if (deliveryOptionId is not ("standard" or "express")) throw new ArgumentException("Unknown delivery option."); lock (_gate) RequireBasket(basketId).DeliveryOptionId = deliveryOptionId; return Task.CompletedTask; }
    public async Task<CommerceQuote> GetQuoteAsync(string basketId, string deliveryOptionId, CancellationToken cancellationToken = default)
    { var basket = await GetBasketAsync(basketId, cancellationToken); var options = await GetDeliveryOptionsAsync(basketId, cancellationToken); var delivery = options.Single(x => x.DeliveryOptionId == deliveryOptionId); var subtotal = basket.Items.Sum(x => x.TotalPrice); return new CommerceQuote($"quote_{Guid.NewGuid():N}", basketId, MerchantId, MerchantName, "GBP", basket.Items, subtotal, delivery.Fee, subtotal + delivery.Fee, deliveryOptionId, DateTimeOffset.UtcNow.AddMinutes(10)); }
    public Task PrepareCheckoutAsync(PurchaseIntent intent, CancellationToken cancellationToken = default)
    { if (intent.QuoteExpiresAt < DateTimeOffset.UtcNow) throw new InvalidOperationException("Quote expired."); if (intent.MerchantId != MerchantId) throw new InvalidOperationException("Wrong merchant."); return Task.CompletedTask; }
    public async Task<ConnectorPurchaseResult> ExecutePurchaseAsync(PurchaseIntent intent, PurchaseAuthorisation authorisation, CancellationToken cancellationToken = default)
    {
        if (!_authorisations.Verify(intent, authorisation, DateTimeOffset.UtcNow)) throw new UnauthorizedAccessException("Purchase authorisation is invalid or does not match the intent.");
        lock (_gate) if (_purchases.TryGetValue(intent.IdempotencyKey, out var existing)) return existing;
        PlatformPaymentResult payment;
        _durability.BeginPaymentSubmission(intent, _payments.ProviderName);
        try
        {
            payment = await _payments.ProcessAsync(intent, cancellationToken);
            _durability.RecordPaymentResult(intent, payment);
        }
        catch
        {
            _durability.RecordPaymentUnknown(intent, "PAYMENT_OUTCOME_UNKNOWN");
            lock (_gate) _purchases[intent.IdempotencyKey] = new ConnectorPurchaseResult(ConnectorPurchaseStatus.Unknown, null, null, "PAYMENT_OUTCOME_UNKNOWN", null);
            throw;
        }
        var result = payment.Status switch
        {
            PlatformPaymentStatus.Succeeded => new ConnectorPurchaseResult(ConnectorPurchaseStatus.Succeeded, payment.ProviderReference, null, null,
                new PurchaseReceipt($"receipt_{Guid.NewGuid():N}", intent.PurchaseIntentId, MerchantId, intent.TotalAmount, intent.Currency, payment.ProviderReference ?? "", DateTimeOffset.UtcNow)),
            PlatformPaymentStatus.RequiresAction => new ConnectorPurchaseResult(ConnectorPurchaseStatus.RequiresAction, payment.ProviderReference, payment.RequiredAction, null, null),
            PlatformPaymentStatus.Processing => new ConnectorPurchaseResult(ConnectorPurchaseStatus.Processing, payment.ProviderReference, null, null, null),
            PlatformPaymentStatus.Failed => new ConnectorPurchaseResult(ConnectorPurchaseStatus.Failed, payment.ProviderReference, null, payment.FailureReason, null),
            _ => new ConnectorPurchaseResult(ConnectorPurchaseStatus.Unknown, payment.ProviderReference, null, payment.FailureReason, null)
        };
        lock (_gate) _purchases[intent.IdempotencyKey] = result;
        return result;
    }
    private MutableBasket RequireBasket(string id) => _baskets.TryGetValue(id, out var b) ? b : throw new KeyNotFoundException("Basket not found.");
    private Product RequireProduct(string id) => _catalogue.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException("Product not found.");
    private Basket Snapshot(MutableBasket b) => new(b.Id, MerchantId, b.Items.Select(kv => { var p = RequireProduct(kv.Key); return new BasketItem(p.ProductId, p.Description, kv.Value.Quantity, p.UnitPrice, p.UnitPrice * kv.Value.Quantity, kv.Value.Substitute); }).ToList());
    private static IEnumerable<Product> DefaultCatalogue() => [
        new("milk-2l", "Semi-skimmed milk 2L", 1.65m, "GBP", 100, new HashSet<string>{"milk","dairy"}),
        new("bread-wholemeal", "Wholemeal bread", 1.40m, "GBP", 100, new HashSet<string>{"bread","bakery"}),
        new("eggs-12", "Free range eggs 12 pack", 3.20m, "GBP", 100, new HashSet<string>{"eggs"}),
        new("bananas-1kg", "Bananas 1kg", 1.15m, "GBP", 100, new HashSet<string>{"banana","fruit"}),
        new("rice-5kg", "Basmati rice 5kg", 8.50m, "GBP", 100, new HashSet<string>{"rice","grocery"}),
        new("chicken-breast-500g","Chicken breast 500g",4.75m,"GBP",100,new HashSet<string>{"chicken","meat"}),
        new("tortilla-wraps-8","Soft tortilla wraps 8 pack",1.80m,"GBP",100,new HashSet<string>{"wrap","wraps","tortilla"}),
        new("lettuce-iceberg","Iceberg lettuce",0.95m,"GBP",100,new HashSet<string>{"lettuce","salad"}),
        new("tomatoes-6","Salad tomatoes 6 pack",1.25m,"GBP",100,new HashSet<string>{"tomato","tomatoes","salad"}),
        new("garlic-sauce","Garlic mayonnaise sauce",1.50m,"GBP",100,new HashSet<string>{"sauce","mayonnaise"})];
}

public sealed class MockPlatformPaymentProcessor : IPlatformPaymentProcessor
{
    private readonly object _gate = new(); private readonly Dictionary<string, PlatformPaymentResult> _results = new();
    public int SubmissionCount { get; private set; } public PlatformPaymentStatus NextStatus { get; set; } = PlatformPaymentStatus.Succeeded;
    public string ProviderName => "Mock";
    public Task<PlatformPaymentResult> ProcessAsync(PurchaseIntent intent, CancellationToken cancellationToken = default)
    { lock (_gate) { if (_results.TryGetValue(intent.IdempotencyKey, out var existing)) return Task.FromResult(existing); SubmissionCount++; var result = new PlatformPaymentResult(NextStatus, $"demo_pay_{intent.PurchaseIntentId}", NextStatus == PlatformPaymentStatus.RequiresAction ? "demo_client_secret" : null, NextStatus == PlatformPaymentStatus.Failed ? "SIMULATED_DECLINE" : null); _results[intent.IdempotencyKey] = result; return Task.FromResult(result); } }
}
