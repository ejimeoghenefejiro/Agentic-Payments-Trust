using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Intelligence.Investigation;

/// <summary>
/// Orchestrates the investigation tools into the doc's reasoning loop: fetch history -> build
/// profile -> detect anomalies -> calculate risk -> collect evidence -> recommend. This
/// implementation is a deterministic pipeline (reproducible, testable, no LLM cost) — the same
/// tool methods are also exposed as Semantic Kernel functions in InvestigationTools.cs, the seam
/// where a real LLM-driven agent could later choose which tools to call and in what order,
/// mirroring how AgentTrust.Agents.SemanticKernelPaymentAgent already works for payment intents.
/// </summary>
public sealed class InvestigationAgent
{
    private readonly ITransactionEventStore _eventStore;
    private readonly TransactionRiskEngine _riskEngine;

    public InvestigationAgent(ITransactionEventStore eventStore, TransactionRiskEngine riskEngine)
    {
        _eventStore = eventStore;
        _riskEngine = riskEngine;
    }

    public RiskAssessment Investigate(TransactionEvent candidate)
    {
        var history = _eventStore.GetCustomerHistory(candidate.CustomerId)
            .Where(e => e.TransactionId != candidate.TransactionId)
            .ToList();
        var profile = BehaviourProfileBuilder.BuildCustomerProfile(candidate.CustomerId, history);

        var assessment = _riskEngine.Assess(candidate, profile, history);

        _eventStore.Record(candidate);
        return assessment;
    }
}
