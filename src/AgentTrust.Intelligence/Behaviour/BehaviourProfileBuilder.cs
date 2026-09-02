namespace AgentTrust.Intelligence.Behaviour;

/// <summary>Builds behaviour profiles from raw historical events. Percentile-based rather than
/// simple min/max so a single historical outlier doesn't permanently widen "normal."</summary>
public static class BehaviourProfileBuilder
{
    public static CustomerBehaviourProfile BuildCustomerProfile(string customerId, IReadOnlyList<TransactionEvent> history)
    {
        if (history.Count == 0)
        {
            return new CustomerBehaviourProfile(customerId, 0, 0, Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), TimeOnly.MinValue, TimeOnly.MaxValue, 0);
        }

        var amounts = history.Select(h => h.Amount).OrderBy(a => a).ToList();
        var min = Percentile(amounts, 5);
        var max = Percentile(amounts, 95);

        var hours = history.Select(h => (decimal)h.Timestamp.UtcDateTime.Hour).OrderBy(h => h).ToList();
        var windowStart = new TimeOnly((int)Percentile(hours, 5), 0);
        var windowEnd = new TimeOnly((int)Percentile(hours, 95), 59);

        return new CustomerBehaviourProfile(
            customerId,
            min,
            max,
            history.Select(h => h.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            history.Select(h => h.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            history.Select(h => h.MerchantId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            history.Where(h => h.BeneficiaryId is not null).Select(h => h.BeneficiaryId!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            windowStart,
            windowEnd,
            history.Count);
    }

    public static MerchantBehaviourProfile BuildMerchantProfile(string merchantId, IReadOnlyList<TransactionEvent> history, int observationDays)
    {
        if (history.Count == 0 || observationDays <= 0)
        {
            return new MerchantBehaviourProfile(merchantId, 0, 0, 0, Array.Empty<string>(), 0);
        }

        var refundRate = (double)history.Count(h => h.WasRefunded) / history.Count;

        return new MerchantBehaviourProfile(
            merchantId,
            (double)history.Count / observationDays,
            history.Average(h => h.Amount),
            refundRate,
            history.Select(h => h.Location).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            history.Count);
    }

    private static decimal Percentile(List<decimal> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;
        var rank = percentile / 100.0 * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedValues[lower];
        var fraction = (decimal)(rank - lower);
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
    }
}
