using AgentTrust.Intelligence.Anomaly;

namespace AgentTrust.Intelligence.Risk;

/// <summary>
/// A longer-horizon risk view of an entity (customer/merchant/device) built from behavioural
/// change, graph relationships and peer comparison — distinct from RiskAssessment, which scores
/// one specific candidate transaction. Reuses RiskFactor as the common currency across every
/// engine in this layer so all of them read the same shape.
/// </summary>
public sealed record EntityRiskAssessment(
    string EntityId,
    int RiskScore,
    IntelligenceRecommendation Recommendation,
    IReadOnlyList<RiskFactor> Factors);
