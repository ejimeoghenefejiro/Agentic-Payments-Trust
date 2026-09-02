using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Investigation;

public interface ITransactionEventStore
{
    void Record(TransactionEvent transactionEvent);
    IReadOnlyList<TransactionEvent> GetCustomerHistory(string customerId);
    IReadOnlyList<TransactionEvent> GetMerchantHistory(string merchantId);
    IReadOnlyList<TransactionEvent> GetDeviceHistory(string deviceId);
    IReadOnlyList<TransactionEvent> GetBeneficiaryHistory(string beneficiaryId);
}

public sealed class InMemoryTransactionEventStore : ITransactionEventStore
{
    private readonly List<TransactionEvent> _events = new();

    public void Record(TransactionEvent transactionEvent) => _events.Add(transactionEvent);

    public IReadOnlyList<TransactionEvent> GetCustomerHistory(string customerId) =>
        _events.Where(e => e.CustomerId == customerId).OrderBy(e => e.Timestamp).ToList();

    public IReadOnlyList<TransactionEvent> GetMerchantHistory(string merchantId) =>
        _events.Where(e => e.MerchantId == merchantId).OrderBy(e => e.Timestamp).ToList();

    public IReadOnlyList<TransactionEvent> GetDeviceHistory(string deviceId) =>
        _events.Where(e => e.DeviceId == deviceId).OrderBy(e => e.Timestamp).ToList();

    public IReadOnlyList<TransactionEvent> GetBeneficiaryHistory(string beneficiaryId) =>
        _events.Where(e => e.BeneficiaryId == beneficiaryId).OrderBy(e => e.Timestamp).ToList();
}
