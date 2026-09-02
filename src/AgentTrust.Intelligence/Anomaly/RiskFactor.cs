namespace AgentTrust.Intelligence.Anomaly;

/// <summary>Matches the doc's evidence-based risk JSON: {"factor": "NEW_DEVICE", "weight": 0.17}.</summary>
public sealed record RiskFactor(string Factor, double Weight, string Detail);

public interface IAnomalyDetector
{
    IReadOnlyList<RiskFactor> Detect(Behaviour.TransactionEvent candidate, Behaviour.CustomerBehaviourProfile? profile, IReadOnlyList<Behaviour.TransactionEvent> recentHistory);
}
