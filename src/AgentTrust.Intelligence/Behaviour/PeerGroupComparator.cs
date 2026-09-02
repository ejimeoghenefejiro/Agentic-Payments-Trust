namespace AgentTrust.Intelligence.Behaviour;

public sealed record PeerGroupDeviation(string Aspect, string Detail, double Severity);

/// <summary>
/// Cross-sectional comparison against similar entities, distinct from BehaviourDeviationService
/// (which compares an entity against its *own* history). A merchant can be perfectly consistent
/// with its own past and still be an outlier against its peer group — e.g. a refund rate that
/// never changed but was always high relative to other merchants in the same category.
/// </summary>
public static class PeerGroupComparator
{
    public static IReadOnlyList<PeerGroupDeviation> CompareMerchantToPeers(MerchantBehaviourProfile subject, IReadOnlyList<MerchantBehaviourProfile> peers)
    {
        var deviations = new List<PeerGroupDeviation>();
        if (peers.Count == 0)
        {
            return deviations;
        }

        var peerAverageRefundRate = peers.Average(p => p.RefundRate);
        if (peerAverageRefundRate > 0 && subject.RefundRate >= peerAverageRefundRate * 2)
        {
            deviations.Add(new PeerGroupDeviation("REFUND_RATE_VS_PEERS",
                $"Refund rate {subject.RefundRate:P0} vs peer-group average {peerAverageRefundRate:P0}",
                Math.Min(1.0, (double)(subject.RefundRate / peerAverageRefundRate - 1) / 3)));
        }

        var peerAverageAmount = peers.Average(p => (double)p.AverageTransactionAmount);
        if (peerAverageAmount > 0 && (double)subject.AverageTransactionAmount >= peerAverageAmount * 3)
        {
            deviations.Add(new PeerGroupDeviation("AVERAGE_AMOUNT_VS_PEERS",
                $"Average transaction {subject.AverageTransactionAmount:C} vs peer-group average {peerAverageAmount:C}",
                Math.Min(1.0, ((double)subject.AverageTransactionAmount / peerAverageAmount - 1) / 5)));
        }

        return deviations;
    }
}
