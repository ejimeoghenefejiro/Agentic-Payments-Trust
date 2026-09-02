using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class GraphIntelligenceTests
{
    private static TransactionEvent Event(string customer, string merchant, string device, string ip) =>
        new($"tx_{Guid.NewGuid():N}", customer, merchant, 22m, "GBP", DateTimeOffset.UtcNow, device, ip, "UK", null, null, false, 0);

    [Fact]
    public void CommunityRiskAnalyzerFlagsDocFraudRingExample()
    {
        // "Merchant A -> 87 customer accounts -> 12 devices -> 3 IP addresses -> same settlement account."
        var events = new List<TransactionEvent>();
        for (var i = 0; i < 87; i++)
        {
            events.Add(Event($"C{i}", "MerchantA", $"D{i % 12}", $"IP{i % 3}"));
        }
        var settlement = new Dictionary<string, string> { ["MerchantA"] = "SettlementX" };
        var graph = RelationshipAnalyzer.BuildGraph(events, settlement);

        var findings = CommunityRiskAnalyzer.AnalyzeMerchant(graph, "MerchantA");

        Assert.Contains(findings, f => f.Aspect == "DEVICE_COLLAPSE");
        Assert.Contains(findings, f => f.Aspect == "IP_COLLAPSE");
        Assert.Contains(findings, f => f.Aspect == "SINGLE_SETTLEMENT_ACCOUNT");
    }

    [Fact]
    public void CommunityRiskAnalyzerFindsNothingForAnOrdinaryMerchant()
    {
        // 30 customers, each with their own device and IP — no collapse.
        var events = Enumerable.Range(0, 30).Select(i => Event($"C{i}", "MerchantB", $"D{i}", $"IP{i}")).ToList();
        var graph = RelationshipAnalyzer.BuildGraph(events);

        var findings = CommunityRiskAnalyzer.AnalyzeMerchant(graph, "MerchantB");

        Assert.Empty(findings);
    }

    [Fact]
    public void RelationshipAnalyzerFindsDevicesSharedAcrossMultipleCustomers()
    {
        var events = new List<TransactionEvent>
        {
            Event("C1", "MerchantA", "SharedDevice", "IP1"),
            Event("C2", "MerchantA", "SharedDevice", "IP2"),
            Event("C3", "MerchantA", "OwnDevice", "IP3")
        };
        var graph = RelationshipAnalyzer.BuildGraph(events);

        var shared = RelationshipAnalyzer.FindSharedDevicesForMerchant(graph, "MerchantA");

        Assert.Single(shared);
        Assert.Equal("SharedDevice", shared[0].SharedNodeId);
        Assert.Equal(2, shared[0].ConnectedCustomers.Count);
    }

    [Fact]
    public void CommunityRiskRequiresAMinimumCustomerCount()
    {
        // Only 3 customers all sharing one device — a real collapse ratio, but too few
        // customers to be a meaningful fraud-ring signal rather than noise.
        var events = new[] { Event("C1", "TinyMerchant", "D1", "IP1"), Event("C2", "TinyMerchant", "D1", "IP1"), Event("C3", "TinyMerchant", "D1", "IP1") };
        var graph = RelationshipAnalyzer.BuildGraph(events);

        var findings = CommunityRiskAnalyzer.AnalyzeMerchant(graph, "TinyMerchant");

        Assert.Empty(findings);
    }
}
