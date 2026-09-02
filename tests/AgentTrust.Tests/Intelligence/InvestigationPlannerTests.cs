using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Risk;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class InvestigationPlannerTests
{
    private static TransactionRiskEngine BuildRiskEngine() => new(
        new IAnomalyDetector[] { new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector() },
        new EvidenceCollector());

    [Fact]
    public void AmbiguousScoreTriggersGraphDeepDiveAndCanRaiseTheFinalRecommendation()
    {
        var eventStore = new InMemoryTransactionEventStore();
        var baseTime = new DateTimeOffset(2027, 5, 1, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 30; i++)
        {
            eventStore.Record(new TransactionEvent($"tx_hist_{i}", "C1", "M1", 100m, "GBP", baseTime.AddDays(i), "D_normal", "1.1.1.1", "Manchester", null, null, false, 0));
        }
        var planner = new InvestigationPlanner(new InvestigationAgent(eventStore, BuildRiskEngine()), new DeviceRiskEngine(sharedCustomerThreshold: 3));

        // A single, mild deviation (new device only) should land in the ambiguous band, not
        // "obviously fine" or "obviously bad" on its own.
        var candidate = new TransactionEvent("tx_candidate", "C1", "M1", 105m, "GBP", baseTime.AddDays(40), "SharedDevice", "1.1.1.1", "Manchester", null, null, false, 0);

        // The graph shows this "new device" is actually shared with several other customers.
        var otherCustomerEvents = Enumerable.Range(0, 4)
            .Select(i => new TransactionEvent($"tx_other_{i}", $"Other{i}", "M1", 100m, "GBP", baseTime, "SharedDevice", $"2.2.2.{i}", "Elsewhere", null, null, false, 0))
            .ToList();
        var graph = RelationshipAnalyzer.BuildGraph(otherCustomerEvents.Append(candidate));

        var result = planner.Investigate(candidate, graph);

        Assert.Contains(result.Steps, s => s.Tool == "analyse_transaction_graph");
        Assert.True(result.FinalAssessment.RiskScore >= result.InitialAssessment.RiskScore);
        Assert.Contains(result.FinalAssessment.RiskFactors, f => f.Factor == "DEVICE_SHARED_ACROSS_CUSTOMERS");
    }

    [Fact]
    public void ClearlyLowRiskTransactionSkipsFurtherInvestigation()
    {
        var eventStore = new InMemoryTransactionEventStore();
        var baseTime = new DateTimeOffset(2027, 5, 1, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 30; i++)
        {
            eventStore.Record(new TransactionEvent($"tx_hist_{i}", "C1", "M1", 100m, "GBP", baseTime.AddDays(i), "D1", "1.1.1.1", "Manchester", "B1", null, false, 0));
        }
        var planner = new InvestigationPlanner(new InvestigationAgent(eventStore, BuildRiskEngine()));
        var candidate = new TransactionEvent("tx_ordinary", "C1", "M1", 100m, "GBP", baseTime.AddDays(40), "D1", "1.1.1.1", "Manchester", "B1", null, false, 0);

        var result = planner.Investigate(candidate, graph: null);

        Assert.DoesNotContain(result.Steps, s => s.Tool == "analyse_transaction_graph");
        Assert.Equal(result.InitialAssessment.RiskScore, result.FinalAssessment.RiskScore);
    }

    [Fact]
    public void MerchantInvestigationAgentReproducesDocSurgeExampleEndToEnd()
    {
        var baseline = Enumerable.Range(0, 150).Select(i =>
            new TransactionEvent($"tx_base_{i}", $"RegularC{i}", "M-surge", 22m, "GBP", DateTimeOffset.UtcNow.AddDays(-30), $"RD{i}", $"RIP{i}", "UK", null, null, false, 0)).ToList();
        var recent = Enumerable.Range(0, 90).Select(i =>
            new TransactionEvent($"tx_recent_{i}", $"NewC{i}", "M-surge", 480m, "GBP", DateTimeOffset.UtcNow, $"D{i % 8}", $"IP{i % 3}", "??", null, null, i % 6 == 0, 0)).ToList();

        var agent = new MerchantInvestigationAgent();
        // 150 baseline transactions over 30 days (5/day) vs. 90 recent transactions in 1 day —
        // an 18x daily-volume spike, matching the doc's 150 -> 4,300 tx/day shape.
        var assessment = agent.Investigate("M-surge", baseline, recent, baselineObservationDays: 30, recentObservationDays: 1,
            merchantSettlementAccounts: new Dictionary<string, string> { ["M-surge"] = "SettlementZ" });

        Assert.Equal(IntelligenceRecommendation.Escalate, assessment.Recommendation);
        Assert.Contains(assessment.Factors, f => f.Factor is "TRANSACTION_VOLUME" or "AVERAGE_AMOUNT" or "REFUND_RATE");
        Assert.Contains(assessment.Factors, f => f.Factor is "DEVICE_COLLAPSE" or "IP_COLLAPSE" or "SINGLE_SETTLEMENT_ACCOUNT");
    }
}
