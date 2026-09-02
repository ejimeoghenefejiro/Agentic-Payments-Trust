using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Graph;

public sealed record SharedEntityFinding(string SharedNodeId, GraphNodeType SharedNodeType, IReadOnlyList<string> ConnectedCustomers);

/// <summary>Builds a FinancialGraph from raw transaction events and answers relationship
/// questions a single transaction can never reveal on its own — the doc's example: a merchant
/// whose 87 customer accounts collapse down to 12 devices and 3 IP addresses.</summary>
public static class RelationshipAnalyzer
{
    public static FinancialGraph BuildGraph(IEnumerable<TransactionEvent> events, IReadOnlyDictionary<string, string>? merchantSettlementAccounts = null)
    {
        var graph = new FinancialGraph();
        merchantSettlementAccounts ??= new Dictionary<string, string>();

        foreach (var e in events)
        {
            graph.AddNode(e.CustomerId, GraphNodeType.Customer);
            graph.AddNode(e.DeviceId, GraphNodeType.Device);
            graph.AddNode(e.MerchantId, GraphNodeType.Merchant);
            graph.AddNode(e.IpAddress, GraphNodeType.IpAddress);

            graph.AddEdge(e.CustomerId, e.DeviceId, "USES_DEVICE");
            graph.AddEdge(e.CustomerId, e.MerchantId, "TRANSACTS_WITH");
            graph.AddEdge(e.CustomerId, e.IpAddress, "CONNECTS_FROM");

            if (e.BeneficiaryId is not null)
            {
                graph.AddNode(e.BeneficiaryId, GraphNodeType.Beneficiary);
                graph.AddEdge(e.CustomerId, e.BeneficiaryId, "PAYS");
            }

            if (merchantSettlementAccounts.TryGetValue(e.MerchantId, out var settlementAccount))
            {
                graph.AddNode(settlementAccount, GraphNodeType.SettlementAccount);
                graph.AddEdge(e.MerchantId, settlementAccount, "SETTLES_TO");
            }
        }

        return graph;
    }

    /// <summary>Finds devices used by more than one distinct customer connected to the given
    /// merchant — a device shared across otherwise-unrelated customers is a classic
    /// fraud-ring/account-farming signal.</summary>
    public static IReadOnlyList<SharedEntityFinding> FindSharedDevicesForMerchant(FinancialGraph graph, string merchantId, int minSharingCustomers = 2)
    {
        var customers = graph.EdgesTo(merchantId, "TRANSACTS_WITH").Select(e => e.FromNodeId).Distinct().ToList();
        var deviceToCustomers = new Dictionary<string, List<string>>();

        foreach (var customerId in customers)
        {
            foreach (var deviceId in graph.Neighbors(customerId, "USES_DEVICE"))
            {
                if (!deviceToCustomers.TryGetValue(deviceId, out var list))
                {
                    deviceToCustomers[deviceId] = list = new List<string>();
                }
                list.Add(customerId);
            }
        }

        return deviceToCustomers
            .Where(kv => kv.Value.Count >= minSharingCustomers)
            .Select(kv => new SharedEntityFinding(kv.Key, GraphNodeType.Device, kv.Value))
            .ToList();
    }
}
