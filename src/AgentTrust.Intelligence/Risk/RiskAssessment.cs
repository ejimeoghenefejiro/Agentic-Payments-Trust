using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Anomaly;

namespace AgentTrust.Intelligence.Risk;

public enum IntelligenceRecommendation
{
    Approve,
    Escalate
}

/// <summary>
/// The intelligence layer's structured, evidence-backed output — directly mirrors the doc's JSON
/// example (riskScore, confidence, recommendation, riskFactors, evidence). This is advisory only:
/// Recommendation is what the AI thinks should happen, never what is authorised to happen. The
/// unchanged, frozen TrustFramework/PolicyEngine makes that call using EvidenceReferences as the
/// EvidenceManifest for the eventual TransactionIntent.
/// </summary>
public sealed record RiskAssessment(
    string TransactionId,
    int RiskScore,
    double Confidence,
    IntelligenceRecommendation Recommendation,
    IReadOnlyList<RiskFactor> RiskFactors,
    IReadOnlyList<EvidenceItem> EvidenceReferences);
