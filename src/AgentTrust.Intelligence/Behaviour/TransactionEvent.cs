namespace AgentTrust.Intelligence.Behaviour;

/// <summary>
/// A single observed transaction, richer than the trust layer's TransactionIntent — this is the
/// raw material the intelligence layer reasons over (device, IP, location, beneficiary age,
/// prior failures), not the narrow fields a policy engine needs. The intelligence layer never
/// authorises anything itself; it turns events like this into a RiskAssessment (Risk/RiskAssessment.cs)
/// that becomes evidence for the existing, unchanged TrustFramework to decide on.
/// </summary>
public sealed record TransactionEvent(
    string TransactionId,
    string CustomerId,
    string MerchantId,
    decimal Amount,
    string Currency,
    DateTimeOffset Timestamp,
    string DeviceId,
    string IpAddress,
    string Location,
    string? BeneficiaryId,
    DateTimeOffset? BeneficiaryCreatedAt,
    bool WasRefunded,
    int PriorFailedAttempts);
