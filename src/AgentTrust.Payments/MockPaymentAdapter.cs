using AgentTrust.Core.Models;

namespace AgentTrust.Payments;

/// <summary>
/// Sandbox/mock PSP adapter. Simulates provider responses deterministically so
/// experiments are reproducible. A transaction ID present in <see cref="ForcedFailures"/>
/// always returns a provider failure, modelling scenario S15 (valid trust decision,
/// PSP execution failure).
/// </summary>
public sealed class MockPaymentAdapter : IPaymentAdapter
{
    public HashSet<string> ForcedFailures { get; } = new();

    /// <summary>Transaction IDs actually submitted for execution, in call order. Tests use this
    /// to prove the adapter is never invoked for a denied or still-pending-escalation transaction.</summary>
    public List<string> SubmittedTransactionIds { get; } = new();

    public PaymentResult Submit(TransactionIntent intent)
    {
        SubmittedTransactionIds.Add(intent.TransactionId);
        if (ForcedFailures.Contains(intent.TransactionId))
        {
            return new PaymentResult(intent.TransactionId, PaymentStatus.Failure,
                ProviderReference: string.Empty, FailureReason: "SIMULATED_PROVIDER_OUTAGE");
        }

        var reference = $"psp_{intent.TransactionId}_{intent.Amount:0.00}";
        return new PaymentResult(intent.TransactionId, PaymentStatus.Success, reference, FailureReason: null);
    }
}
