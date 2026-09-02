namespace AgentTrust.PaymentMethods;

/// <summary>
/// The only place raw card details are ever allowed to exist: as parameters to this call, for
/// the duration of that call. The result is a token plus display metadata — the raw card number
/// and CVV are never returned, logged, or stored anywhere in this codebase. A real implementation
/// would call out to an actual PCI-compliant provider (Stripe, Adyen, etc.); this mock simulates
/// one deterministically for tests/demos without ever needing a real provider account.
/// </summary>
public interface ICardTokenizationProvider
{
    TokenizationResult Tokenize(string cardNumber, string cvv, int expiryMonth, int expiryYear);
}

public sealed record TokenizationResult(string Token, string CardBrand, string Last4, int ExpiryMonth, int ExpiryYear);

public sealed class MockCardTokenizationProvider : ICardTokenizationProvider
{
    public TokenizationResult Tokenize(string cardNumber, string cvv, int expiryMonth, int expiryYear)
    {
        if (string.IsNullOrWhiteSpace(cardNumber) || cardNumber.Length < 4)
        {
            throw new ArgumentException("Card number is invalid.", nameof(cardNumber));
        }

        var brand = cardNumber.StartsWith('4') ? "Visa" : cardNumber.StartsWith('5') ? "Mastercard" : "Unknown";
        var last4 = cardNumber[^4..];
        var token = $"tok_{Guid.NewGuid():N}";

        // cardNumber and cvv go out of scope here and are never retained, logged, or returned.
        return new TokenizationResult(token, brand, last4, expiryMonth, expiryYear);
    }
}
