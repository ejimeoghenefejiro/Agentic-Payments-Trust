using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Anomaly;

/// <summary>
/// Flags a transaction amount that deviates materially from the customer's typical range, scaled
/// by how far outside that range it falls rather than a flat yes/no — matches the doc's
/// "TRANSACTION_AMOUNT_ANOMALY" factor with a graded weight (0.29 in its worked example).
/// </summary>
public sealed class AmountAnomalyDetector : IAnomalyDetector
{
    public IReadOnlyList<RiskFactor> Detect(TransactionEvent candidate, CustomerBehaviourProfile? profile, IReadOnlyList<TransactionEvent> recentHistory)
    {
        if (profile is null || profile.SampleSize == 0 || profile.TypicalMaxAmount <= 0)
        {
            return Array.Empty<RiskFactor>();
        }

        if (profile.IsWithinTypicalAmount(candidate.Amount))
        {
            return Array.Empty<RiskFactor>();
        }

        var ratio = candidate.Amount > profile.TypicalMaxAmount
            ? (double)(candidate.Amount / profile.TypicalMaxAmount)
            : (double)(profile.TypicalMinAmount == 0 ? 1 : profile.TypicalMinAmount / Math.Max(candidate.Amount, 0.01m));

        var weight = Math.Min(0.35, 0.05 * Math.Log2(Math.Max(ratio, 1.0) + 1));

        return new[]
        {
            new RiskFactor("TRANSACTION_AMOUNT_ANOMALY", weight,
                $"Amount {candidate.Amount:C} is {ratio:F1}x outside the typical range {profile.TypicalMinAmount:C}-{profile.TypicalMaxAmount:C}")
        };
    }
}
