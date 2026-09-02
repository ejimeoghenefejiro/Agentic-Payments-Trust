using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Intelligence.Investigation;

/// <summary>
/// A specialist agent for the merchant side of the doc's investigation examples (section 6):
/// given a merchant's historical baseline and a recent window of activity, builds both profiles,
/// checks for a material behavioural shift, and checks the relationship graph for a fraud-ring
/// pattern (many accounts collapsing to few devices/IPs funnelling to one settlement account).
/// InvestigationAgent (Phase 1) investigates one candidate transaction for one customer; this
/// investigates a merchant's overall recent behaviour.
/// </summary>
public sealed class MerchantInvestigationAgent
{
    private readonly MerchantRiskEngine _merchantRiskEngine;

    public MerchantInvestigationAgent(MerchantRiskEngine? merchantRiskEngine = null) =>
        _merchantRiskEngine = merchantRiskEngine ?? new MerchantRiskEngine();

    public EntityRiskAssessment Investigate(
        string merchantId,
        IReadOnlyList<TransactionEvent> baselineWindow,
        IReadOnlyList<TransactionEvent> recentWindow,
        int baselineObservationDays,
        int recentObservationDays,
        IReadOnlyDictionary<string, string>? merchantSettlementAccounts = null,
        IReadOnlyList<MerchantBehaviourProfile>? peers = null)
    {
        var baseline = BehaviourProfileBuilder.BuildMerchantProfile(merchantId, baselineWindow, baselineObservationDays);
        var current = BehaviourProfileBuilder.BuildMerchantProfile(merchantId, recentWindow, recentObservationDays);
        var graph = RelationshipAnalyzer.BuildGraph(recentWindow, merchantSettlementAccounts);

        return _merchantRiskEngine.Assess(merchantId, baseline, current, graph, peers);
    }
}
