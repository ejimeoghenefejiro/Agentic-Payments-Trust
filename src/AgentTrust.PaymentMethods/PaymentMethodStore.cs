namespace AgentTrust.PaymentMethods;

public interface IPaymentMethodStore
{
    void Save(PaymentMethod method);
    PaymentMethod? Find(string paymentMethodId);
    IReadOnlyList<PaymentMethod> FindByPrincipal(string principalId);
}

public sealed class InMemoryPaymentMethodStore : IPaymentMethodStore
{
    private readonly Dictionary<string, PaymentMethod> _methods = new();

    public void Save(PaymentMethod method) => _methods[method.PaymentMethodId] = method;

    public PaymentMethod? Find(string paymentMethodId) => _methods.GetValueOrDefault(paymentMethodId);

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

    public void Revoke(string paymentMethodId)
    {
        var method = _store.Find(paymentMethodId);
        if (method is not null)
        {
            _store.Save(method with { Status = PaymentMethodStatus.Revoked });
        }
    }
}
