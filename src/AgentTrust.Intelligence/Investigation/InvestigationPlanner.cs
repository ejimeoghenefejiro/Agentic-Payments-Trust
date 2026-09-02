using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Intelligence.Investigation;

public sealed record InvestigationStep(string Tool, string Rationale, string ResultSummary);

public sealed record MultiStepInvestigationResult(
    RiskAssessment InitialAssessment,
    IReadOnlyList<InvestigationStep> Steps,
    RiskAssessment FinalAssessment);

/// <summary>
/// The doc's reasoning loop made real: receive task -> choose a tool -> inspect the result ->
/// choose another tool if needed -> collect evidence -> generate a recommendation. A single-pass
/// InvestigationAgent (Phase 1) always runs exactly one tool. This planner only reaches for
/// device-graph and merchant-graph tools when the initial transaction-level score is ambiguous
/// (neither clearly fine nor clearly bad) — cheap, unambiguous cases don't pay for extra
/// investigation steps, mirroring how a human analyst would only dig further when genuinely
/// unsure.
/// </summary>
public sealed class InvestigationPlanner
{
    private readonly InvestigationAgent _investigationAgent;
    private readonly DeviceRiskEngine _deviceRiskEngine;
    private readonly int _ambiguousLowerBound;
    private readonly int _ambiguousUpperBound;
    private readonly int _finalEscalationThreshold;

    public InvestigationPlanner(
        InvestigationAgent investigationAgent,
        DeviceRiskEngine? deviceRiskEngine = null,
        int ambiguousLowerBound = 20,
        int ambiguousUpperBound = 70,
        int finalEscalationThreshold = 50)
    {
        _investigationAgent = investigationAgent;
        _deviceRiskEngine = deviceRiskEngine ?? new DeviceRiskEngine();
        _ambiguousLowerBound = ambiguousLowerBound;
        _ambiguousUpperBound = ambiguousUpperBound;
        _finalEscalationThreshold = finalEscalationThreshold;
    }

    public MultiStepInvestigationResult Investigate(TransactionEvent candidate, FinancialGraph? graph)
    {
        var steps = new List<InvestigationStep>();

        var initial = _investigationAgent.Investigate(candidate);
        steps.Add(new InvestigationStep("calculate_risk",
            "Form an initial hypothesis from the transaction and the customer's own history",
            $"score={initial.RiskScore}, recommendation={initial.Recommendation}, factors={initial.RiskFactors.Count}"));

        var isAmbiguous = initial.RiskScore >= _ambiguousLowerBound && initial.RiskScore < _ambiguousUpperBound;
        if (!isAmbiguous || graph is null)
        {
            steps.Add(new InvestigationStep("generate_recommendation",
                isAmbiguous ? "No relationship graph available to investigate further" : "Initial score is not ambiguous; no further investigation needed",
                $"final={initial.Recommendation}"));
            return new MultiStepInvestigationResult(initial, steps, initial);
        }

        steps.Add(new InvestigationStep("analyse_transaction_graph",
            "Initial score is ambiguous — testing the hypothesis that this device is shared with other customer accounts",
            ""));
        var deviceAssessment = _deviceRiskEngine.Assess(graph, candidate.DeviceId);
        steps[^1] = steps[^1] with { ResultSummary = $"device risk score={deviceAssessment.RiskScore}, factors={deviceAssessment.Factors.Count}" };

        var combinedFactors = initial.RiskFactors.Concat(deviceAssessment.Factors).ToList();
        var combinedEvidence = initial.EvidenceReferences.Concat(
            deviceAssessment.Factors.Select(f => new EvidenceItem($"device-graph-{candidate.DeviceId}", "graph_relationship", f.Detail, true))).ToList();
        var combinedScore = Math.Min(100, initial.RiskScore + deviceAssessment.RiskScore / 2);
        var finalRecommendation = combinedScore >= _finalEscalationThreshold ? IntelligenceRecommendation.Escalate : IntelligenceRecommendation.Approve;
        var finalConfidence = Math.Min(1.0, initial.Confidence + 0.1);

        var finalAssessment = initial with
        {
            RiskScore = combinedScore,
            Recommendation = finalRecommendation,
            RiskFactors = combinedFactors,
            EvidenceReferences = combinedEvidence,
            Confidence = finalConfidence
        };

        steps.Add(new InvestigationStep("generate_recommendation",
            "Combining transaction-level and graph-level evidence",
            $"final={finalAssessment.Recommendation} (score {initial.RiskScore} -> {combinedScore})"));

        return new MultiStepInvestigationResult(initial, steps, finalAssessment);
    }
}
