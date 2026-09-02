namespace AgentTrust.PaymentMethods;

public enum PaymentMethodStatus
{
    Active,
    Expired,
    Revoked
}

/// <summary>
/// What the platform is allowed to retain about a connected card: a provider token and
/// display-only metadata. Never a raw PAN or CVV — see ICardTokenizationProvider, which takes
/// raw card details as transient method parameters and returns only this shape.
/// </summary>
public sealed record PaymentMethod(
    string PaymentMethodId,
    string PrincipalId,
    string Provider,
    string Token,
    string CardBrand,
    string Last4,
    int ExpiryMonth,
    int ExpiryYear,
    PaymentMethodStatus Status)
{
    public bool IsUsable(DateOnly asOf) =>
        Status == PaymentMethodStatus.Active &&
        (ExpiryYear > asOf.Year || (ExpiryYear == asOf.Year && ExpiryMonth >= asOf.Month));
}
