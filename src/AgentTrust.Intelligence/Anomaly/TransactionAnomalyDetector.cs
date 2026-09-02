using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Anomaly;

/// <summary>
/// Contextual anomaly detection matching the doc's night-time scenario: several individually
/// small deviations (new device, new beneficiary, unusual time, unusual location, a beneficiary
/// added minutes ago, prior failed attempts) considered together rather than one threshold rule.
/// </summary>
public sealed class TransactionAnomalyDetector : IAnomalyDetector
{
    public IReadOnlyList<RiskFactor> Detect(TransactionEvent candidate, CustomerBehaviourProfile? profile, IReadOnlyList<TransactionEvent> recentHistory)
    {
        var factors = new List<RiskFactor>();
        if (profile is null || profile.SampleSize == 0)
        {
            factors.Add(new RiskFactor("NO_BEHAVIOUR_HISTORY", 0.10, "No established behaviour profile for this customer"));
            return factors;
        }

        if (!profile.IsKnownDevice(candidate.DeviceId))
        {
            factors.Add(new RiskFactor("NEW_DEVICE", 0.17, $"Device {candidate.DeviceId} not seen in customer history"));
        }

        if (!profile.IsKnownLocation(candidate.Location))
        {
            factors.Add(new RiskFactor("UNUSUAL_LOCATION", 0.18, $"Location {candidate.Location} not among typical locations"));
        }

        if (candidate.BeneficiaryId is not null && !profile.IsKnownBeneficiary(candidate.BeneficiaryId))
        {
            factors.Add(new RiskFactor("NEW_BENEFICIARY", 0.21, $"Beneficiary {candidate.BeneficiaryId} not among regular beneficiaries"));
        }

        if (candidate.BeneficiaryCreatedAt is not null &&
            candidate.Timestamp - candidate.BeneficiaryCreatedAt.Value <= TimeSpan.FromMinutes(15))
        {
            var minutesAgo = (candidate.Timestamp - candidate.BeneficiaryCreatedAt.Value).TotalMinutes;
            factors.Add(new RiskFactor("RECENTLY_ADDED_BENEFICIARY", 0.25, $"Beneficiary added {minutesAgo:F0} minutes before this transaction"));
        }

        if (!profile.IsWithinTypicalWindow(TimeOnly.FromDateTime(candidate.Timestamp.UtcDateTime)))
        {
            factors.Add(new RiskFactor("UNUSUAL_TIME", 0.12, $"Transaction at {candidate.Timestamp.UtcDateTime:HH:mm} is outside the typical {profile.TypicalWindowStart}-{profile.TypicalWindowEnd} window"));
        }

        if (candidate.PriorFailedAttempts > 0)
        {
            factors.Add(new RiskFactor("PRIOR_FAILED_ATTEMPTS", Math.Min(0.10 * candidate.PriorFailedAttempts, 0.30),
                $"{candidate.PriorFailedAttempts} failed attempt(s) immediately before this transaction"));
        }

        return factors;
    }
}
