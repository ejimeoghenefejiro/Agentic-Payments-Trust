namespace AgentTrust.Intelligence.Behaviour;

public sealed record BehaviourDeviation(string Aspect, string Detail, double Severity);

/// <summary>
/// Compares a current snapshot against a baseline profile and flags material shifts — the doc's
/// merchant example: 150 tx/day at £22 average and 2% refunds baseline, suddenly 4,300 tx/day at
/// £480 average and 18% refunds. Works for either a customer or merchant profile snapshot pair.
/// </summary>
public static class BehaviourDeviationService
{
    public static IReadOnlyList<BehaviourDeviation> CompareMerchantProfiles(MerchantBehaviourProfile baseline, MerchantBehaviourProfile current)
    {
        var deviations = new List<BehaviourDeviation>();

        if (baseline.AverageDailyTransactionCount > 0)
        {
            var volumeRatio = current.AverageDailyTransactionCount / baseline.AverageDailyTransactionCount;
            if (volumeRatio >= 3.0)
            {
                deviations.Add(new BehaviourDeviation("TRANSACTION_VOLUME",
                    $"Daily volume {current.AverageDailyTransactionCount:F0} vs baseline {baseline.AverageDailyTransactionCount:F0} ({volumeRatio:F1}x)",
                    Math.Min(1.0, (volumeRatio - 1) / 10)));
            }
        }

        if (baseline.AverageTransactionAmount > 0)
        {
            var amountRatio = (double)(current.AverageTransactionAmount / baseline.AverageTransactionAmount);
            if (amountRatio >= 2.0)
            {
                deviations.Add(new BehaviourDeviation("AVERAGE_AMOUNT",
                    $"Average amount {current.AverageTransactionAmount:C} vs baseline {baseline.AverageTransactionAmount:C} ({amountRatio:F1}x)",
                    Math.Min(1.0, (amountRatio - 1) / 5)));
            }
        }

        var refundDelta = current.RefundRate - baseline.RefundRate;
        if (refundDelta >= 0.05)
        {
            deviations.Add(new BehaviourDeviation("REFUND_RATE",
                $"Refund rate {current.RefundRate:P0} vs baseline {baseline.RefundRate:P0}",
                Math.Min(1.0, refundDelta * 5)));
        }

        return deviations;
    }

    /// <summary>
    /// Behavioural-change detection for a customer: "behaviour should also change over time
    /// rather than assuming a person's historical behaviour remains permanently fixed" — this
    /// compares two profile snapshots of the *same* customer taken at different times (see
    /// ProfileHistoryStore) rather than flagging every transaction against a single fixed
    /// lifetime baseline.
    /// </summary>
    public static IReadOnlyList<BehaviourDeviation> CompareCustomerProfiles(CustomerBehaviourProfile baseline, CustomerBehaviourProfile current)
    {
        var deviations = new List<BehaviourDeviation>();
        if (baseline.SampleSize == 0 || current.SampleSize == 0)
        {
            return deviations;
        }

        if (baseline.TypicalMaxAmount > 0)
        {
            var maxRatio = (double)(current.TypicalMaxAmount / baseline.TypicalMaxAmount);
            if (maxRatio >= 2.0 || maxRatio <= 0.5)
            {
                deviations.Add(new BehaviourDeviation("SPENDING_RANGE_SHIFT",
                    $"Typical max amount moved from {baseline.TypicalMaxAmount:C} to {current.TypicalMaxAmount:C} ({maxRatio:F1}x)",
                    Math.Min(1.0, Math.Abs(maxRatio - 1) / 3)));
            }
        }

        var deviceOverlap = baseline.TypicalDevices.Intersect(current.TypicalDevices, StringComparer.OrdinalIgnoreCase).Count();
        var deviceUnion = baseline.TypicalDevices.Union(current.TypicalDevices, StringComparer.OrdinalIgnoreCase).Count();
        if (deviceUnion > 0 && (double)deviceOverlap / deviceUnion < 0.3)
        {
            deviations.Add(new BehaviourDeviation("DEVICE_SET_CHANGED",
                $"Device overlap with prior baseline is only {(double)deviceOverlap / deviceUnion:P0}",
                0.6));
        }

        var locationOverlap = baseline.TypicalLocations.Intersect(current.TypicalLocations, StringComparer.OrdinalIgnoreCase).Any();
        if (!locationOverlap && baseline.TypicalLocations.Count > 0 && current.TypicalLocations.Count > 0)
        {
            deviations.Add(new BehaviourDeviation("LOCATION_SET_CHANGED",
                $"No overlap between prior locations ({string.Join(", ", baseline.TypicalLocations)}) and current ({string.Join(", ", current.TypicalLocations)})",
                0.5));
        }

        return deviations;
    }
}
