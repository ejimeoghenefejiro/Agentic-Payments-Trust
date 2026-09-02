using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;

namespace AgentTrust.Intelligence.Risk;

/// <summary>
/// A customer's longer-horizon risk profile: has their own behaviour shifted materially over
/// time (BehaviourDeviationService.CompareCustomerProfiles), and do any of the devices they use
/// carry device-level risk (shared with other customers, via DeviceRiskEngine)? Distinct from
/// TransactionRiskEngine, which only scores one specific candidate transaction.
/// </summary>
public sealed class CustomerRiskEngine
{
    private readonly DeviceRiskEngine _deviceRiskEngine;
    private readonly int _escalationThreshold;

    public CustomerRiskEngine(DeviceRiskEngine? deviceRiskEngine = null, int escalationThreshold = 50)
    {
        _deviceRiskEngine = deviceRiskEngine ?? new DeviceRiskEngine();
        _escalationThreshold = escalationThreshold;
    }

    public EntityRiskAssessment Assess(string customerId, CustomerBehaviourProfile? historicalBaseline, CustomerBehaviourProfile currentProfile, FinancialGraph? graph = null)
    {
        var factors = new List<RiskFactor>();

        if (historicalBaseline is not null)
        {
            foreach (var d in BehaviourDeviationService.CompareCustomerProfiles(historicalBaseline, currentProfile))
            {
                factors.Add(new RiskFactor(d.Aspect, d.Severity, d.Detail));
            }
        }

        if (graph is not null)
        {
            foreach (var deviceId in currentProfile.TypicalDevices)
            {
                var deviceAssessment = _deviceRiskEngine.Assess(graph, deviceId);
                factors.AddRange(deviceAssessment.Factors);
            }
        }

        var riskScore = (int)Math.Round(Math.Min(1.0, factors.Sum(f => f.Weight) / 2) * 100);
        var recommendation = riskScore >= _escalationThreshold ? IntelligenceRecommendation.Escalate : IntelligenceRecommendation.Approve;
        return new EntityRiskAssessment(customerId, riskScore, recommendation, factors);
    }
}
