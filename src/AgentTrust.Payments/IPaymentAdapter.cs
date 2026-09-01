using AgentTrust.Core.Models;

namespace AgentTrust.Payments;

public interface IPaymentAdapter
{
    PaymentResult Submit(TransactionIntent intent);
}
