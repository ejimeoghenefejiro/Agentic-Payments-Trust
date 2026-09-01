namespace AgentTrust.Core.Models;

public enum PaymentStatus
{
    NotAttempted,
    Success,
    Failure
}

public sealed record PaymentResult(
    string TransactionId,
    PaymentStatus Status,
    string ProviderReference,
    string? FailureReason);
