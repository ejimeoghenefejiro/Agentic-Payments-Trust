using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;

namespace AgentTrust.Intelligence.Risk;

/// <summary>
/// Combines every merchant-facing signal built in this layer: the doc's own-history shift
/// (BehaviourDeviationService), the graph fraud-ring pattern (CommunityRiskAnalyzer), and
/// optionally a peer-group comparison — reproducing the section-6/7 merchant example fully.
/// </summary>
public sealed class MerchantRiskEngine
{
    private readonly int _escalationThreshold;

    public MerchantRiskEngine(int escalationThreshold = 50) => _escalationThreshold = escalationThreshold;

    public EntityRiskAssessment Assess(
        string merchantId,
        MerchantBehaviourProfile baseline,
        MerchantBehaviourProfile current,
        FinancialGraph? graph = null,
        IReadOnlyList<MerchantBehaviourProfile>? peers = null)
    {
        var factors = new List<RiskFactor>();

        foreach (var d in BehaviourDeviationService.CompareMerchantProfiles(baseline, current))
        {
            factors.Add(new RiskFactor(d.Aspect, d.Severity, d.Detail));
        }

        if (graph is not null)
        {
            foreach (var f in CommunityRiskAnalyzer.AnalyzeMerchant(graph, merchantId))
            {
                factors.Add(new RiskFactor(f.Aspect, f.Severity, f.Detail));
            }
        }

        if (peers is not null)
        {
            foreach (var p in PeerGroupComparator.CompareMerchantToPeers(current, peers))
            {
                factors.Add(new RiskFactor(p.Aspect, p.Severity, p.Detail));
            }
        }

        var riskScore = (int)Math.Round(Math.Min(1.0, factors.Sum(f => f.Weight) / 2) * 100);
        var recommendation = riskScore >= _escalationThreshold ? IntelligenceRecommendation.Escalate : IntelligenceRecommendation.Approve;
        return new EntityRiskAssessment(merchantId, riskScore, recommendation, factors);
    }
}
