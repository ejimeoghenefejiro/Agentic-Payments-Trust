using System.Security.Cryptography;
using System.Text;

namespace AgentTrust.Mandates;

public enum OneOffAuthorisationStatus { Active, Consumed, Expired, Revoked }
public sealed record OneOffAuthorisation(string AuthorisationId, string ExecutionId, string MandateId,
    int MandateVersion, string TransactionFingerprint, decimal Amount, string Currency, string Merchant,
    string PaymentMethodId, string Approver, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    OneOffAuthorisationStatus Status, DateTimeOffset? ConsumedAt);

public interface IOneOffAuthorisationStore
{
    void Save(OneOffAuthorisation authorisation);
    OneOffAuthorisation? Find(string authorisationId);
    bool TryConsume(string authorisationId, string expectedFingerprint, DateTimeOffset now, out OneOffAuthorisation? consumed);
}

public sealed class InMemoryOneOffAuthorisationStore : IOneOffAuthorisationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OneOffAuthorisation> _items = new();
    public void Save(OneOffAuthorisation item) { lock (_gate) _items[item.AuthorisationId] = item; }
    public OneOffAuthorisation? Find(string id) { lock (_gate) return _items.GetValueOrDefault(id); }
    public bool TryConsume(string id, string fingerprint, DateTimeOffset now, out OneOffAuthorisation? consumed)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item) || item.Status != OneOffAuthorisationStatus.Active
                || now > item.ExpiresAt || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(item.TransactionFingerprint), Encoding.UTF8.GetBytes(fingerprint)))
            { consumed = null; return false; }
            consumed = item with { Status = OneOffAuthorisationStatus.Consumed, ConsumedAt = now };
            _items[id] = consumed; return true;
        }
    }
}

public static class TransactionFingerprint
{
    public static string Create(FinancialMandate mandate, string executionId, decimal amount, string currency,
        IReadOnlyDictionary<string, string> context)
    {
        var pairs = string.Join("&", context.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var value = string.Join("|", executionId, mandate.MandateId, mandate.Version, mandate.AgentId,
            mandate.PrincipalId, mandate.Merchant, mandate.PaymentMethodId,
            amount.ToString(System.Globalization.CultureInfo.InvariantCulture), currency.ToUpperInvariant(), pairs);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
