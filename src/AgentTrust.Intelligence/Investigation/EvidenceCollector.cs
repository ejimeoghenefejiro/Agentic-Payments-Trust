using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Intelligence.Investigation;

/// <summary>
/// Turns the risk factors that fired into concrete, referenceable evidence items — reuses
/// AgentTrust.Core.Models.EvidenceItem directly, so a RiskAssessment's evidence can be passed
/// straight into an EvidenceManifest for the (unchanged) trust layer without any translation.
/// </summary>
public sealed class EvidenceCollector : IEvidenceCollector
{
    public IReadOnlyList<EvidenceItem> Collect(TransactionEvent candidate, IReadOnlyList<RiskFactor> factors)
    {
        var evidence = new List<EvidenceItem>
        {
            new($"transaction-event-{candidate.TransactionId}", "transaction_event",
                $"Candidate transaction {candidate.Amount:C} at {candidate.Timestamp:u}", true)
        };

        foreach (var factor in factors)
        {
            var evidenceId = factor.Factor switch
            {
                "NEW_DEVICE" => $"device-history-{candidate.DeviceId}",
                "NEW_BENEFICIARY" or "RECENTLY_ADDED_BENEFICIARY" => $"beneficiary-creation-{candidate.BeneficiaryId}",
                "UNUSUAL_LOCATION" => $"location-history-{candidate.CustomerId}",
                "UNUSUAL_TIME" => $"customer-profile-{candidate.CustomerId}",
                "TRANSACTION_AMOUNT_ANOMALY" => $"transaction-history-{candidate.CustomerId}",
                "PRIOR_FAILED_ATTEMPTS" => $"attempt-log-{candidate.TransactionId}",
                _ => $"risk-factor-{factor.Factor.ToLowerInvariant()}-{candidate.TransactionId}"
            };
            evidence.Add(new EvidenceItem(evidenceId, "risk_factor", factor.Detail, true));
        }

        return evidence;
    }
}
