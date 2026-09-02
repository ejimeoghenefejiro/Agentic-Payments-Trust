using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Risk;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class TransactionRiskEngineTests
{
    private static TransactionRiskEngine BuildEngine(int escalationThreshold = 50) => new(
        new IAnomalyDetector[] { new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector() },
        new EvidenceCollector(),
        escalationThreshold);

    private static CustomerBehaviourProfile NormalProfile() => new(
        "C10391", 30m, 400m,
        new[] { "Manchester", "Salford" },
        new[] { "D44", "D71" },
        new[] { "M14", "M18", "M33" },
        new[] { "B101", "B201" },
        new TimeOnly(7, 0), new TimeOnly(23, 0),
        40);

    [Fact]
    public void DocNightTimeScenarioProducesHighRiskEscalateRecommendationWithEvidence()
    {
        var candidate = new TransactionEvent(
            "tx_night", "C10391", "M14", 8700m, "GBP",
            new DateTimeOffset(2027, 6, 7, 3, 41, 0, TimeSpan.Zero),
            "D999-unknown", "203.0.113.9", "Lagos",
            "B999-new", new DateTimeOffset(2027, 6, 7, 3, 39, 0, TimeSpan.Zero),
            false, 3);

        var assessment = BuildEngine().Assess(candidate, NormalProfile(), Array.Empty<TransactionEvent>());

        Assert.Equal(IntelligenceRecommendation.Escalate, assessment.Recommendation);
        Assert.True(assessment.RiskScore >= 50, $"expected risk score >= 50, got {assessment.RiskScore}");
        Assert.True(assessment.RiskFactors.Count >= 5);
        Assert.True(assessment.Confidence > 0.5);
        Assert.Contains(assessment.EvidenceReferences, e => e.Type == "transaction_event");
        Assert.Contains(assessment.EvidenceReferences, e => e.Type == "risk_factor");
    }

    [Fact]
    public void OrdinaryTransactionProducesLowRiskApproveRecommendation()
    {
        var candidate = new TransactionEvent(
            "tx_ordinary", "C10391", "M14", 120m, "GBP",
            new DateTimeOffset(2027, 6, 7, 14, 0, 0, TimeSpan.Zero),
            "D44", "1.2.3.4", "Manchester", "B101",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), false, 0);

        var assessment = BuildEngine().Assess(candidate, NormalProfile(), Array.Empty<TransactionEvent>());

        Assert.Equal(IntelligenceRecommendation.Approve, assessment.Recommendation);
        Assert.Equal(0, assessment.RiskScore);
    }

    [Fact]
    public void InvestigationAgentBuildsProfileFromStoredHistoryAndRecordsTheCandidate()
    {
        var store = new InMemoryTransactionEventStore();
        var baseTime = new DateTimeOffset(2027, 5, 1, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 10; i++)
        {
            store.Record(new TransactionEvent($"tx_hist_{i}", "C1", "M14", 100m, "GBP", baseTime.AddDays(i), "D1", "1.1.1.1", "Manchester", "B1", null, false, 0));
        }

        var agent = new InvestigationAgent(store, BuildEngine());
        var candidate = new TransactionEvent("tx_new", "C1", "M14", 9000m, "GBP", baseTime.AddDays(20).AddHours(15), "D-new", "9.9.9.9", "Unknown", "B-new", baseTime.AddDays(20).AddHours(15).AddMinutes(-1), false, 2);

        var assessment = agent.Investigate(candidate);

        Assert.Equal(IntelligenceRecommendation.Escalate, assessment.Recommendation);
        Assert.Contains(store.GetCustomerHistory("C1"), e => e.TransactionId == "tx_new");
    }
}
