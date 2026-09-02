using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Anomaly;

/// <summary>Flags abnormal transaction velocity: too many transactions, or too much value, in a
/// short trailing window relative to the customer's own recent baseline.</summary>
public sealed class VelocityDetector : IAnomalyDetector
{
    private readonly TimeSpan _window;
    private readonly int _countThreshold;
    private readonly decimal _amountThreshold;

    public VelocityDetector(TimeSpan? window = null, int countThreshold = 5, decimal amountThreshold = 10000m)
    {
        _window = window ?? TimeSpan.FromHours(1);
        _countThreshold = countThreshold;
        _amountThreshold = amountThreshold;
    }

    public IReadOnlyList<RiskFactor> Detect(TransactionEvent candidate, CustomerBehaviourProfile? profile, IReadOnlyList<TransactionEvent> recentHistory)
    {
        var windowStart = candidate.Timestamp - _window;
        var inWindow = recentHistory
            .Where(h => h.CustomerId == candidate.CustomerId && h.Timestamp >= windowStart && h.Timestamp <= candidate.Timestamp)
            .ToList();

        var count = inWindow.Count + 1; // include the candidate itself
        var sum = inWindow.Sum(h => h.Amount) + candidate.Amount;

        var factors = new List<RiskFactor>();
        if (count > _countThreshold)
        {
            factors.Add(new RiskFactor("HIGH_TRANSACTION_VELOCITY", Math.Min(0.30, 0.05 * (count - _countThreshold)),
                $"{count} transactions within {_window.TotalMinutes:F0} minutes (threshold {_countThreshold})"));
        }
        if (sum > _amountThreshold)
        {
            factors.Add(new RiskFactor("HIGH_VALUE_VELOCITY", Math.Min(0.30, (double)((sum - _amountThreshold) / _amountThreshold) * 0.2),
                $"Cumulative {sum:C} within {_window.TotalMinutes:F0} minutes (threshold {_amountThreshold:C})"));
        }
        return factors;
    }
}
