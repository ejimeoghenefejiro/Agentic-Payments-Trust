namespace AgentTrust.PaymentMethods;

public interface IPaymentMethodStore
{
    void Save(PaymentMethod method);
    PaymentMethod? Find(string paymentMethodId);
    PaymentMethod? FindByProviderToken(string provider,string providerToken);
    IReadOnlyList<PaymentMethod> FindByPrincipal(string principalId);
}

public sealed class InMemoryPaymentMethodStore : IPaymentMethodStore
{
    private readonly Dictionary<string, PaymentMethod> _methods = new();

    public void Save(PaymentMethod method) => _methods[method.PaymentMethodId] = method;

    public PaymentMethod? Find(string paymentMethodId) => _methods.GetValueOrDefault(paymentMethodId);
    public PaymentMethod? FindByProviderToken(string provider,string token)=>_methods.Values.FirstOrDefault(x=>
        string.Equals(x.Provider,provider,StringComparison.OrdinalIgnoreCase)&&x.Token==token);

    public IReadOnlyList<PaymentMethod> FindByPrincipal(string principalId) =>
        _methods.Values.Where(m => m.PrincipalId == principalId).ToList();
}

/// <summary>
/// The doc's "connect card" flow: User enters card -> payment provider -> provider tokenises
/// card -> payment token -> platform stores token. Raw card details never survive past this
/// single call into ICardTokenizationProvider.
/// </summary>
public sealed class PaymentMethodService
{
    private readonly ICardTokenizationProvider _tokenizationProvider;
    private readonly IPaymentMethodStore _store;

    public PaymentMethodService(ICardTokenizationProvider tokenizationProvider, IPaymentMethodStore store)
    {
        _tokenizationProvider = tokenizationProvider;
        _store = store;
    }

    public PaymentMethod ConnectCard(string principalId, string provider, string cardNumber, string cvv, int expiryMonth, int expiryYear)
    {
        var tokenization = _tokenizationProvider.Tokenize(cardNumber, cvv, expiryMonth, expiryYear);
        var method = new PaymentMethod(
            $"pm_{Guid.NewGuid():N}", principalId, provider, tokenization.Token,
            tokenization.CardBrand, tokenization.Last4, tokenization.ExpiryMonth, tokenization.ExpiryYear,
            PaymentMethodStatus.Active);
        _store.Save(method);
        return method;
    }

    /// <summary>Production-preferred connection flow. A PSP-hosted field tokenises the card in
    /// the browser, so this backend receives only the token and display-safe metadata.</summary>
    public PaymentMethod ConnectProviderToken(string principalId, string provider, string providerToken,
        string cardBrand, string last4, int expiryMonth, int expiryYear)
    {
        if (string.IsNullOrWhiteSpace(providerToken)) throw new ArgumentException("Provider token is required.", nameof(providerToken));
        if (last4.Length != 4 || !last4.All(char.IsDigit)) throw new ArgumentException("Last4 must contain four digits.", nameof(last4));
        var existing=_store.FindByProviderToken(provider,providerToken);
        if(existing is not null)
        {
            if(existing.PrincipalId!=principalId)throw new InvalidOperationException("The provider payment method is already registered.");
            if(existing.Status==PaymentMethodStatus.Active)return existing;
            var reactivated=existing with{Status=PaymentMethodStatus.Active,CardBrand=cardBrand,Last4=last4,ExpiryMonth=expiryMonth,ExpiryYear=expiryYear};_store.Save(reactivated);return reactivated;
        }
        var method = new PaymentMethod($"pm_{Guid.NewGuid():N}", principalId, provider, providerToken,
            cardBrand, last4, expiryMonth, expiryYear, PaymentMethodStatus.Active);
        _store.Save(method);
        return method;
    }

    public void Revoke(string paymentMethodId)
    {
        var method = _store.Find(paymentMethodId);
        if (method is not null)
        {
            _store.Save(method with { Status = PaymentMethodStatus.Revoked });
        }
    }
}
