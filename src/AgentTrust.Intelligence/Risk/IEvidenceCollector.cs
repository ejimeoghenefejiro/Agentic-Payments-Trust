using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;

namespace AgentTrust.Intelligence.Risk;

public interface IEvidenceCollector
{
    IReadOnlyList<EvidenceItem> Collect(TransactionEvent candidate, IReadOnlyList<RiskFactor> factors);
}
