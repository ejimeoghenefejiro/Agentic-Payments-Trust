using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Risk;

/// <summary>
/// Aggregates every detector's risk factors into the single structured RiskAssessment described
/// in the doc — the AI generates the intelligence, the (unchanged) trust layer decides what may
/// actually happen. Recommendation here is advisory: it never bypasses PolicyEngine.
/// </summary>
public sealed class TransactionRiskEngine
{
    private readonly IReadOnlyList<IAnomalyDetector> _detectors;
    private readonly IEvidenceCollector _evidenceCollector;
    private readonly int _escalationThreshold;

    public TransactionRiskEngine(IReadOnlyList<IAnomalyDetector> detectors, IEvidenceCollector evidenceCollector, int escalationThreshold = 50)
    {
        _detectors = detectors;
        _evidenceCollector = evidenceCollector;
        _escalationThreshold = escalationThreshold;
    }

    public RiskAssessment Assess(TransactionEvent candidate, CustomerBehaviourProfile? profile, IReadOnlyList<TransactionEvent> recentHistory)
    {
        var factors = _detectors.SelectMany(d => d.Detect(candidate, profile, recentHistory)).ToList();
        var totalWeight = factors.Sum(f => f.Weight);
        var riskScore = (int)Math.Round(Math.Min(1.0, totalWeight) * 100);
        var confidence = Math.Min(1.0, 0.5 + 0.1 * factors.Count);
        var recommendation = riskScore >= _escalationThreshold ? IntelligenceRecommendation.Escalate : IntelligenceRecommendation.Approve;
        var evidence = _evidenceCollector.Collect(candidate, factors);

        return new RiskAssessment(candidate.TransactionId, riskScore, confidence, recommendation, factors, evidence);
    }
}
