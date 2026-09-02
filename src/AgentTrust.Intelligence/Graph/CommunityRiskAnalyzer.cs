namespace AgentTrust.Intelligence.Graph;

public sealed record CommunityRiskFinding(string Aspect, string Detail, double Severity);

/// <summary>
/// The doc's community-fraud-ring pattern, made concrete: "Merchant A -> 87 customer accounts ->
/// 12 devices -> 3 IP addresses -> same settlement account." A high customer-to-device (or
/// customer-to-IP) collapse ratio, especially funnelling to one settlement account, is invisible
/// when transactions are analysed one at a time — it only shows up in the graph.
/// </summary>
public static class CommunityRiskAnalyzer
{
    public static IReadOnlyList<CommunityRiskFinding> AnalyzeMerchant(
        FinancialGraph graph, string merchantId, int minCustomers = 10, double minCollapseRatio = 3.0)
    {
        var findings = new List<CommunityRiskFinding>();
        var customers = graph.EdgesTo(merchantId, "TRANSACTS_WITH").Select(e => e.FromNodeId).Distinct().ToList();
        if (customers.Count < minCustomers)
        {
            return findings;
        }

        var devices = customers.SelectMany(c => graph.Neighbors(c, "USES_DEVICE")).Distinct().ToList();
        var ips = customers.SelectMany(c => graph.Neighbors(c, "CONNECTS_FROM")).Distinct().ToList();

        var deviceRatio = devices.Count == 0 ? double.MaxValue : (double)customers.Count / devices.Count;
        if (deviceRatio >= minCollapseRatio)
        {
            findings.Add(new CommunityRiskFinding("DEVICE_COLLAPSE",
                $"{customers.Count} customer accounts collapse to just {devices.Count} devices ({deviceRatio:F1}x)",
                Math.Min(1.0, deviceRatio / 20)));
        }

        var ipRatio = ips.Count == 0 ? double.MaxValue : (double)customers.Count / ips.Count;
        if (ipRatio >= minCollapseRatio)
        {
            findings.Add(new CommunityRiskFinding("IP_COLLAPSE",
                $"{customers.Count} customer accounts collapse to just {ips.Count} IP addresses ({ipRatio:F1}x)",
                Math.Min(1.0, ipRatio / 20)));
        }

        var settlementAccounts = graph.Neighbors(merchantId, "SETTLES_TO");
        if (settlementAccounts.Count == 1 && (deviceRatio >= minCollapseRatio || ipRatio >= minCollapseRatio))
        {
            findings.Add(new CommunityRiskFinding("SINGLE_SETTLEMENT_ACCOUNT",
                $"All {customers.Count} accounts ultimately settle to the single account {settlementAccounts[0]}",
                0.9));
        }

        return findings;
    }
}
