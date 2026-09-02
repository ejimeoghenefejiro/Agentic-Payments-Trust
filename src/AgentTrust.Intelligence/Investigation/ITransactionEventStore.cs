using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Investigation;

public interface ITransactionEventStore
{
    void Record(TransactionEvent transactionEvent);
    IReadOnlyList<TransactionEvent> GetCustomerHistory(string customerId);
    IReadOnlyList<TransactionEvent> GetMerchantHistory(string merchantId);
}

public sealed class InMemoryTransactionEventStore : ITransactionEventStore
{
    private readonly List<TransactionEvent> _events = new();

    public void Record(TransactionEvent transactionEvent) => _events.Add(transactionEvent);

    public IReadOnlyList<TransactionEvent> GetCustomerHistory(string customerId) =>
        _events.Where(e => e.CustomerId == customerId).OrderBy(e => e.Timestamp).ToList();

    public IReadOnlyList<TransactionEvent> GetMerchantHistory(string merchantId) =>
        _events.Where(e => e.MerchantId == merchantId).OrderBy(e => e.Timestamp).ToList();
}
