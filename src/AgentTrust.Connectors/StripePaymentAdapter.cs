using AgentTrust.Commerce;
using AgentTrust.PaymentMethods;
using Stripe;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentTrust.Connectors;

public enum StripePaymentMode { Test, Live }
public sealed record StripePaymentOptions(StripePaymentMode Mode = StripePaymentMode.Test);

/// <summary>Platform-merchant PSP adapter. It uses only tokenised provider references and never
/// accepts PAN/CVV. External merchant connectors must not use this adapter.</summary>
public sealed class StripePaymentAdapter : IPlatformPaymentProcessor
{
    public string ProviderName => "Stripe";
    private readonly PaymentIntentService _service; private readonly IPaymentMethodStore _methods;
    public StripePaymentAdapter(string secretKey, StripePaymentOptions options, IPaymentMethodStore methods)
    {
        if (string.IsNullOrWhiteSpace(secretKey)) throw new InvalidOperationException("STRIPE_SECRET_KEY is required.");
        if (options.Mode == StripePaymentMode.Test && !secretKey.StartsWith("sk_test_", StringComparison.Ordinal)) throw new InvalidOperationException("Stripe test mode requires a test secret key.");
        if (options.Mode == StripePaymentMode.Live && !secretKey.StartsWith("sk_live_", StringComparison.Ordinal)) throw new InvalidOperationException("Stripe live mode requires a live secret key.");
        _service = new PaymentIntentService(new StripeClient(secretKey)); _methods = methods;
    }
    public async Task<PlatformPaymentResult> ProcessAsync(PurchaseIntent intent, CancellationToken cancellationToken = default)
    {
        var method = _methods.Find(intent.PaymentMethodReference) ?? throw new InvalidOperationException("Payment method not found.");
        if (method.PrincipalId != intent.PrincipalId) throw new UnauthorizedAccessException("Payment method belongs to another principal.");
        var amount = checked((long)decimal.Round(intent.TotalAmount * 100, 0, MidpointRounding.AwayFromZero));
        var payment = await _service.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = amount, Currency = intent.Currency.ToLowerInvariant(), PaymentMethod = method.Token,
            Confirm = true, OffSession = true,
            Metadata = new Dictionary<string, string> { ["purchase_intent_id"] = intent.PurchaseIntentId, ["principal_id"] = intent.PrincipalId }
        }, new RequestOptions { IdempotencyKey = intent.IdempotencyKey }, cancellationToken);
        return payment.Status switch
        {
            "succeeded" => new(PlatformPaymentStatus.Succeeded, payment.Id, null, null),
            "requires_action" => new(PlatformPaymentStatus.RequiresAction, payment.Id, payment.ClientSecret, null),
            "processing" => new(PlatformPaymentStatus.Processing, payment.Id, null, null),
            "requires_payment_method" or "canceled" => new(PlatformPaymentStatus.Failed, payment.Id, null, payment.LastPaymentError?.Message ?? payment.Status),
            _ => new(PlatformPaymentStatus.Unknown, payment.Id, null, payment.Status)
        };
    }
}
