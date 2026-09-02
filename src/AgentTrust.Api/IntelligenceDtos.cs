using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Api;

public sealed record TransactionEventDto(
    string TransactionId, string CustomerId, string MerchantId, decimal Amount, string Currency,
    DateTimeOffset Timestamp, string DeviceId, string IpAddress, string Location,
    string? BeneficiaryId, DateTimeOffset? BeneficiaryCreatedAt, bool WasRefunded = false, int PriorFailedAttempts = 0)
{
    public TransactionEvent ToDomain() => new(
        TransactionId, CustomerId, MerchantId, Amount, Currency, Timestamp, DeviceId, IpAddress, Location,
        BeneficiaryId, BeneficiaryCreatedAt, WasRefunded, PriorFailedAttempts);
}

public sealed record MerchantInvestigateRequest(
    DateTimeOffset CutoffDate,
    int BaselineObservationDays,
    int RecentObservationDays,
    string? SettlementAccountId);

public sealed record FeedbackRequestDto(string TransactionId, string AiRecommendation, string ActualOutcome, string? Notes);
