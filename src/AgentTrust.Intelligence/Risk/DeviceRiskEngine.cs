using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Graph;

namespace AgentTrust.Intelligence.Risk;

/// <summary>Device intelligence: a device used by many otherwise-unrelated customer accounts is
/// a classic account-farming/fraud-ring signal, invisible from any single transaction.</summary>
public sealed class DeviceRiskEngine
{
    private readonly int _sharedCustomerThreshold;
    private readonly int _escalationThreshold;

    public DeviceRiskEngine(int sharedCustomerThreshold = 3, int escalationThreshold = 50)
    {
        _sharedCustomerThreshold = sharedCustomerThreshold;
        _escalationThreshold = escalationThreshold;
    }

    public EntityRiskAssessment Assess(FinancialGraph graph, string deviceId)
    {
        var customers = graph.EdgesTo(deviceId, "USES_DEVICE").Select(e => e.FromNodeId).Distinct().ToList();
        var factors = new List<RiskFactor>();

        if (customers.Count >= _sharedCustomerThreshold)
        {
            var weight = Math.Min(1.0, 0.15 * customers.Count);
            factors.Add(new RiskFactor("DEVICE_SHARED_ACROSS_CUSTOMERS", weight,
                $"Device {deviceId} is used by {customers.Count} distinct customer accounts"));
        }

        var riskScore = (int)Math.Round(Math.Min(1.0, factors.Sum(f => f.Weight)) * 100);
        var recommendation = riskScore >= _escalationThreshold ? IntelligenceRecommendation.Escalate : IntelligenceRecommendation.Approve;
        return new EntityRiskAssessment(deviceId, riskScore, recommendation, factors);
    }
}
