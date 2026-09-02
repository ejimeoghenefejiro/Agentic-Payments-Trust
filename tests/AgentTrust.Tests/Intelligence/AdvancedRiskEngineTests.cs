using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Risk;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class AdvancedRiskEngineTests
{
    private static TransactionEvent Event(string customer, string merchant, string device, string ip, decimal amount = 22m) =>
        new($"tx_{Guid.NewGuid():N}", customer, merchant, amount, "GBP", DateTimeOffset.UtcNow, device, ip, "UK", null, null, false, 0);

    [Fact]
    public void MerchantRiskEngineFlagsDocSurgeExampleAsHighRisk()
    {
        // Doc section 6: baseline 150 tx/day, £22 average, 2% refunds. Shift to 4,300 tx/day,
        // £480 average, 18% refunds, plus a graph fraud-ring pattern on the recent window.
        var baseline = new MerchantBehaviourProfile("M-surge", 150, 22m, 0.02, new[] { "UK" }, 4500);
        var current = new MerchantBehaviourProfile("M-surge", 4300, 480m, 0.18, new[] { "UK" }, 4300);
        var recentEvents = Enumerable.Range(0, 60).Select(i => Event($"C{i}", "M-surge", $"D{i % 6}", $"IP{i % 2}", 480m)).ToList();
        var graph = RelationshipAnalyzer.BuildGraph(recentEvents, new Dictionary<string, string> { ["M-surge"] = "SettlementY" });

        var assessment = new MerchantRiskEngine().Assess("M-surge", baseline, current, graph);

        Assert.Equal(IntelligenceRecommendation.Escalate, assessment.Recommendation);
        Assert.True(assessment.RiskScore >= 50);
        Assert.Contains(assessment.Factors, f => f.Factor == "TRANSACTION_VOLUME");
        Assert.Contains(assessment.Factors, f => f.Factor == "DEVICE_COLLAPSE");
    }

    [Fact]
    public void MerchantRiskEngineFindsLowRiskForAStableMerchant()
    {
        var baseline = new MerchantBehaviourProfile("M-stable", 150, 22m, 0.02, new[] { "UK" }, 4500);
        var current = new MerchantBehaviourProfile("M-stable", 160, 23m, 0.025, new[] { "UK" }, 160);

        var assessment = new MerchantRiskEngine().Assess("M-stable", baseline, current);

        Assert.Equal(IntelligenceRecommendation.Approve, assessment.Recommendation);
        Assert.Equal(0, assessment.RiskScore);
    }

    [Fact]
    public void DeviceRiskEngineFlagsADeviceSharedAcrossManyCustomers()
    {
        var events = Enumerable.Range(0, 5).Select(i => Event($"C{i}", "M1", "SharedDevice", $"IP{i}")).ToList();
        var graph = RelationshipAnalyzer.BuildGraph(events);

        var assessment = new DeviceRiskEngine(sharedCustomerThreshold: 3).Assess(graph, "SharedDevice");

        Assert.Equal(IntelligenceRecommendation.Escalate, assessment.Recommendation);
        Assert.Contains(assessment.Factors, f => f.Factor == "DEVICE_SHARED_ACROSS_CUSTOMERS");
    }

    [Fact]
    public void DeviceRiskEngineFindsNothingForAnOrdinaryDevice()
    {
        var events = new[] { Event("C1", "M1", "D1", "IP1") };
        var graph = RelationshipAnalyzer.BuildGraph(events);

        var assessment = new DeviceRiskEngine().Assess(graph, "D1");

        Assert.Equal(IntelligenceRecommendation.Approve, assessment.Recommendation);
        Assert.Empty(assessment.Factors);
    }

    [Fact]
    public void CustomerRiskEngineCombinesBehaviouralChangeAndDeviceSharing()
    {
        var oldProfile = new CustomerBehaviourProfile("C1", 30m, 400m, new[] { "Manchester" }, new[] { "D_old" }, new[] { "M1" }, new[] { "B1" }, new TimeOnly(7, 0), new TimeOnly(23, 0), 40);
        var newProfile = oldProfile with { TypicalMaxAmount = 4000m, TypicalDevices = new[] { "SharedDevice" }, TypicalLocations = new[] { "Lagos" } };

        var events = Enumerable.Range(0, 4).Select(i => Event($"Other{i}", "M1", "SharedDevice", $"IP{i}")).ToList();
        var graph = RelationshipAnalyzer.BuildGraph(events);

        var assessment = new CustomerRiskEngine(new DeviceRiskEngine(sharedCustomerThreshold: 3)).Assess("C1", oldProfile, newProfile, graph);

        Assert.Contains(assessment.Factors, f => f.Factor == "SPENDING_RANGE_SHIFT");
        Assert.Contains(assessment.Factors, f => f.Factor == "DEVICE_SHARED_ACROSS_CUSTOMERS");
        Assert.Equal(IntelligenceRecommendation.Escalate, assessment.Recommendation);
    }

    [Fact]
    public void PeerGroupComparatorFlagsAMerchantThatIsAnOutlierAgainstPeersEvenIfConsistentWithItsOwnHistory()
    {
        var subject = new MerchantBehaviourProfile("M1", 150, 22m, 0.15, new[] { "UK" }, 150);
        var peers = new List<MerchantBehaviourProfile>
        {
            new("M2", 140, 20m, 0.02, new[] { "UK" }, 140),
            new("M3", 160, 24m, 0.03, new[] { "UK" }, 160)
        };

        var deviations = PeerGroupComparator.CompareMerchantToPeers(subject, peers);

        Assert.Contains(deviations, d => d.Aspect == "REFUND_RATE_VS_PEERS");
    }
}
