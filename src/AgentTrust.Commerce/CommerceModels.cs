using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentTrust.Commerce;

public sealed record Product(string ProductId, string Description, decimal UnitPrice, string Currency,
    int AvailableQuantity, IReadOnlySet<string> Tags, decimal? CaloriesPerUnit = null,
    decimal? ProteinGramsPerUnit = null, IReadOnlySet<string>? Allergens = null,
    IReadOnlySet<string>? DietaryTags = null);
public sealed record BasketItem(string ProductId, string Description, int Quantity, decimal UnitPrice,
    decimal TotalPrice, bool SubstitutionAllowed, string? SubstituteForProductId = null);
public sealed record Basket(string BasketId, string MerchantId, IReadOnlyList<BasketItem> Items);
public sealed record DeliveryOption(string DeliveryOptionId, string Description, decimal Fee,
    DateTimeOffset WindowStart, DateTimeOffset WindowEnd);
public sealed record CommerceQuote(string QuoteId, string BasketId, string MerchantId, string MerchantName,
    string Currency, IReadOnlyList<BasketItem> Items, decimal Subtotal, decimal DeliveryFee,
    decimal TotalAmount, string DeliveryOptionId, DateTimeOffset ExpiresAt);
public sealed record PurchaseReceipt(string ReceiptId, string PurchaseIntentId, string MerchantId,
    decimal TotalAmount, string Currency, string ProviderReference, DateTimeOffset PurchasedAt);

public sealed record PurchaseIntent(string PurchaseIntentId, string PrincipalId, string AgentId,
    string MandateId, string TaskId, string MerchantId, string MerchantName, string Currency,
    IReadOnlyList<BasketItem> BasketItems, decimal Subtotal, decimal DeliveryFee, decimal TotalAmount,
    string DeliveryAddressReference, string? RequestedDeliveryWindow, string PaymentMethodReference,
    DateTimeOffset CreatedAt, DateTimeOffset QuoteExpiresAt, string IdempotencyKey);

public sealed class PurchaseAuthorisation
{
    internal PurchaseAuthorisation(string authorisationId, string purchaseIntentId, string transactionId,
        string principalId, string agentId, string mandateId, int mandateVersion, string merchantId,
        decimal authorisedAmount, string currency, DateTimeOffset authorisedAt, DateTimeOffset expiresAt,
        string policyVersion, string intentHash, string signature)
    { AuthorisationId = authorisationId; PurchaseIntentId = purchaseIntentId; TransactionId = transactionId;
      PrincipalId = principalId; AgentId = agentId; MandateId = mandateId; MandateVersion = mandateVersion;
      MerchantId = merchantId; AuthorisedAmount = authorisedAmount; Currency = currency;
      AuthorisedAt = authorisedAt; ExpiresAt = expiresAt; PolicyVersion = policyVersion;
      IntentHash = intentHash; Signature = signature; }
    public string AuthorisationId { get; } public string PurchaseIntentId { get; } public string TransactionId { get; }
    public string PrincipalId { get; } public string AgentId { get; } public string MandateId { get; }
    public int MandateVersion { get; } public string MerchantId { get; } public decimal AuthorisedAmount { get; }
    public string Currency { get; } public DateTimeOffset AuthorisedAt { get; } public DateTimeOffset ExpiresAt { get; }
    public string PolicyVersion { get; } public string IntentHash { get; } public string Signature { get; }
}

public static class PurchaseIntentCanonicalizer
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Hash(PurchaseIntent intent)
    {
        var canonical = new
        {
            intent.PurchaseIntentId, intent.PrincipalId, intent.AgentId, intent.MandateId, intent.TaskId,
            intent.MerchantId, intent.MerchantName, Currency = intent.Currency.ToUpperInvariant(),
            Items = intent.BasketItems.OrderBy(x => x.ProductId, StringComparer.Ordinal).Select(x => new
            { x.ProductId, x.Description, x.Quantity, UnitPrice = Money(x.UnitPrice), TotalPrice = Money(x.TotalPrice), x.SubstitutionAllowed, x.SubstituteForProductId }),
            Subtotal = Money(intent.Subtotal), DeliveryFee = Money(intent.DeliveryFee), TotalAmount = Money(intent.TotalAmount),
            intent.DeliveryAddressReference, intent.RequestedDeliveryWindow, intent.PaymentMethodReference,
            intent.CreatedAt, intent.QuoteExpiresAt, intent.IdempotencyKey
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, Options))));
    }
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

public interface IPurchaseAuthorisationService
{
    PurchaseAuthorisation Issue(PurchaseIntent intent, string transactionId, int mandateVersion,
        string policyVersion, DateTimeOffset now, TimeSpan lifetime);
    bool Verify(PurchaseIntent intent, PurchaseAuthorisation authorisation, DateTimeOffset now);
}
public sealed class HmacPurchaseAuthorisationService : IPurchaseAuthorisationService
{
    private readonly byte[] _key;
    public HmacPurchaseAuthorisationService(byte[] key)
    { if (key.Length < 32) throw new ArgumentException("Authorisation key must be at least 256 bits.", nameof(key)); _key = key.ToArray(); }
    public PurchaseAuthorisation Issue(PurchaseIntent intent, string transactionId, int version,
        string policyVersion, DateTimeOffset now, TimeSpan lifetime)
    {
        var hash = PurchaseIntentCanonicalizer.Hash(intent); var id = $"pa_{Guid.NewGuid():N}";
        return new PurchaseAuthorisation(id, intent.PurchaseIntentId, transactionId, intent.PrincipalId,
            intent.AgentId, intent.MandateId, version, intent.MerchantId, intent.TotalAmount, intent.Currency,
            now, now.Add(lifetime), policyVersion, hash, Sign(id, hash, transactionId));
    }
    public bool Verify(PurchaseIntent intent, PurchaseAuthorisation auth, DateTimeOffset now)
    {
        var hash = PurchaseIntentCanonicalizer.Hash(intent);
        var invariantMatch = now <= auth.ExpiresAt && intent.PurchaseIntentId == auth.PurchaseIntentId
            && intent.PrincipalId == auth.PrincipalId && intent.AgentId == auth.AgentId
            && intent.MandateId == auth.MandateId && intent.MerchantId == auth.MerchantId
            && intent.TotalAmount == auth.AuthorisedAmount
            && string.Equals(intent.Currency, auth.Currency, StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(auth.IntentHash));
        return invariantMatch && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(auth.Signature), Convert.FromHexString(Sign(auth.AuthorisationId, hash, auth.TransactionId)));
    }
    private string Sign(string id, string hash, string tx)
    { using var hmac = new HMACSHA256(_key); return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}|{hash}|{tx}"))); }
}
