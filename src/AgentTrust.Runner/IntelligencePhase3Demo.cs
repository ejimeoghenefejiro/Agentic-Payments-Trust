using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Learning;
using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Runner;

/// <summary>
/// Single-command demonstration of Phase 3 (advanced financial intelligence): the doc's own
/// merchant fraud-ring example (section 6/7) investigated end-to-end by MerchantInvestigationAgent
/// — behavioural-change detection plus graph community-risk analysis together — then a
/// multi-step, ambiguous-transaction investigation, then a feedback/model-evaluation pass.
/// </summary>
public static class IntelligencePhase3Demo
{
    public static void Run()
    {
        Step("1. Merchant investigation — the doc's surge-fraud example", "Baseline: 150 tx/day, £22 average, 2% refunds, all UK, distinct devices. Then a sudden shift.");
        var baseline = Enumerable.Range(0, 150).Select(i =>
            new TransactionEvent($"tx_base_{i}", $"RegularC{i}", "M-superstore", 22m, "GBP", DateTimeOffset.UtcNow.AddDays(-30), $"RD{i}", $"RIP{i}", "UK", null, null, false, 0)).ToList();
        var recent = Enumerable.Range(0, 90).Select(i =>
            new TransactionEvent($"tx_recent_{i}", $"NewC{i}", "M-superstore", 480m, "GBP", DateTimeOffset.UtcNow, $"D{i % 8}", $"IP{i % 3}", "??", null, null, i % 6 == 0, 0)).ToList();

        var merchantAgent = new MerchantInvestigationAgent();
        var merchantAssessment = merchantAgent.Investigate("M-superstore", baseline, recent, baselineObservationDays: 30, recentObservationDays: 1,
            merchantSettlementAccounts: new Dictionary<string, string> { ["M-superstore"] = "settlement_shared_1" });

        Print($"Recent window: 90 transactions/day (vs 5/day baseline), £480 average (vs £22), collapsing to 8 devices / 3 IPs, all settling to one account.");
        foreach (var f in merchantAssessment.Factors)
        {
            Print($"  - {f.Factor} (weight {f.Weight:F2}): {f.Detail}");
        }
        Print($"Merchant risk score: {merchantAssessment.RiskScore}/100  Recommendation: {merchantAssessment.Recommendation}");

        Step("2. Multi-step investigation on an ambiguous transaction", "One mild anomaly alone isn't conclusive — the planner digs into the relationship graph before deciding.");
        var eventStore = new InMemoryTransactionEventStore();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-60);
        for (var i = 0; i < 30; i++)
        {
            eventStore.Record(new TransactionEvent($"tx_hist_{i}", "C500", "M-electronics", 100m, "GBP", baseTime.AddDays(i), "D_normal", "9.9.9.9", "Manchester", null, null, false, 0));
        }
        var riskEngine = new TransactionRiskEngine(
            new IAnomalyDetector[] { new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector() },
            new EvidenceCollector());
        var planner = new InvestigationPlanner(new InvestigationAgent(eventStore, riskEngine), new DeviceRiskEngine(sharedCustomerThreshold: 3));
        var ambiguousCandidate = new TransactionEvent("tx_ambiguous", "C500", "M-electronics", 108m, "GBP", DateTimeOffset.UtcNow, "FarmDevice", "9.9.9.9", "Manchester", null, null, false, 0);
        var otherFarmedAccounts = Enumerable.Range(0, 4)
            .Select(i => new TransactionEvent($"tx_farm_{i}", $"FarmC{i}", "M-electronics", 100m, "GBP", baseTime, "FarmDevice", $"5.5.5.{i}", "Elsewhere", null, null, false, 0));
        var graph = RelationshipAnalyzer.BuildGraph(otherFarmedAccounts.Append(ambiguousCandidate));

        var investigation = planner.Investigate(ambiguousCandidate, graph);
        foreach (var step in investigation.Steps)
        {
            Print($"[{step.Tool}] {step.Rationale} -> {step.ResultSummary}");
        }
        Print($"Initial score {investigation.InitialAssessment.RiskScore} -> final score {investigation.FinalAssessment.RiskScore} ({investigation.FinalAssessment.Recommendation})");

        Step("3. Feedback loop and model evaluation", "Recording what actually happened for past recommendations, then scoring the AI against reality.");
        var outcomes = new InMemoryOutcomeStore();
        outcomes.Record(new DecisionFeedback("tx_a", IntelligenceRecommendation.Escalate, ActualOutcome.Suspicious, "confirmed fraud", DateTimeOffset.UtcNow));
        outcomes.Record(new DecisionFeedback("tx_b", IntelligenceRecommendation.Escalate, ActualOutcome.Legitimate, "customer confirmed, false alarm", DateTimeOffset.UtcNow));
        outcomes.Record(new DecisionFeedback("tx_c", IntelligenceRecommendation.Approve, ActualOutcome.Legitimate, null, DateTimeOffset.UtcNow));
        outcomes.Record(new DecisionFeedback("tx_d", IntelligenceRecommendation.Approve, ActualOutcome.Suspicious, "missed — investigate why", DateTimeOffset.UtcNow));
        var evaluation = ModelEvaluation.Evaluate(outcomes.GetAll());
        Print($"Cases: {evaluation.TotalCases}  Precision: {evaluation.Precision:P0}  Recall: {evaluation.Recall:P0}  F1: {evaluation.F1:P2}  Accuracy: {evaluation.Accuracy:P0}");

        Console.WriteLine();
        Console.WriteLine("=== PHASE 3 DEMO RESULT ===");
        Console.WriteLine($"Merchant investigation:     {merchantAssessment.Recommendation} (score {merchantAssessment.RiskScore})");
        Console.WriteLine($"Multi-step investigation:   {investigation.FinalAssessment.Recommendation} (score {investigation.FinalAssessment.RiskScore})");
        Console.WriteLine($"Model evaluation (F1):      {evaluation.F1:P0} over {evaluation.TotalCases} recorded outcomes");
    }

    private static void Step(string title, string detail)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
        Console.WriteLine(detail);
    }

    private static void Print(string line) => Console.WriteLine($"  {line}");
}
