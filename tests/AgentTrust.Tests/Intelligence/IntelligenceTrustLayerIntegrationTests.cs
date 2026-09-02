using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Risk;
using AgentTrust.Orchestration;
using AgentTrust.Payments;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

/// <summary>
/// Demonstrates the doc's architecture end-to-end without touching a single line of the frozen
/// core: AgentTrust.Intelligence produces an advisory RiskAssessment + evidence; that evidence is
/// handed to the SAME TrustFramework/PolicyEngine used everywhere else in this repo, which makes
/// its own, independent, deterministic decision. The two layers agreeing here is not because the
/// policy engine trusts the AI's risk score — it never sees it — but because both correctly
/// identify the same transaction as needing escalation, each on its own separate grounds
/// (behavioural anomalies vs. a hard amount/human-approval threshold).
/// </summary>
public class IntelligenceTrustLayerIntegrationTests
{
    [Fact]
    public void HighRiskIntelligenceRecommendationAndIndependentPolicyEngineBothEscalate()
    {
        // 1. Intelligence layer: investigate the doc's night-time scenario.
        var eventStore = new InMemoryTransactionEventStore();
        var riskEngine = new TransactionRiskEngine(
            new IAnomalyDetector[] { new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector() },
            new EvidenceCollector());
        var investigationAgent = new InvestigationAgent(eventStore, riskEngine);

        var baseTime = new DateTimeOffset(2027, 6, 1, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 20; i++)
        {
            eventStore.Record(new TransactionEvent($"tx_hist_{i}", "C10391", "M14", 150m, "GBP",
                baseTime.AddDays(i), "D44", "1.2.3.4", "Manchester", "B101", null, false, 0));
        }

        var candidate = new TransactionEvent(
            "tx_night_8700", "C10391", "M14", 8700m, "GBP",
            new DateTimeOffset(2027, 6, 25, 3, 41, 0, TimeSpan.Zero),
            "D999-unknown", "203.0.113.9", "Lagos",
            "B999-new", new DateTimeOffset(2027, 6, 25, 3, 39, 0, TimeSpan.Zero),
            false, 3);

        var assessment = investigationAgent.Investigate(candidate);
        Assert.Equal(IntelligenceRecommendation.Escalate, assessment.Recommendation);

        // 2. Translate into the trust layer's own vocabulary. The intelligence layer's evidence
        // becomes the EvidenceManifest's cited evidence — nothing else about it is trusted.
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        var ledger = new InMemoryTransactionLedger();
        var framework = new TrustFramework(agents, bindings, authorities, ledger, new MockPaymentAdapter());

        agents.Register(new AgentIdentity("agt_c10391", "org_c10391", "consumer", "production",
            CredentialStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-12-31T00:00:00Z"), "ca"));
        bindings.Bind(new PrincipalBinding("agt_c10391", "org_c10391", DateTimeOffset.UtcNow, true, "kyc-doc"));
        authorities.Grant(new DelegatedAuthority(
            "auth_c10391", "agt_c10391", new[] { "transfer:funds" }, 10000m, 20000m,
            Array.Empty<string>(), Array.Empty<string>(), "NG", null, null,
            1000m, // human-approval threshold — far below the £8,700 candidate regardless of risk score
            DateOnly.Parse("2027-12-31"), false));

        var intent = new TransactionIntent(
            candidate.TransactionId, "agt_c10391", "org_c10391", "transfer:funds", candidate.MerchantId,
            "transfer", candidate.Amount, "AI-recommended escalation: " + string.Join(", ", assessment.RiskFactors.Select(f => f.Factor)),
            assessment.EvidenceReferences, candidate.Timestamp, candidate.TransactionId);
        var manifest = new EvidenceManifest(candidate.TransactionId, assessment.EvidenceReferences.ToList(), Array.Empty<string>());

        // 3. The frozen, unmodified trust layer decides — independently.
        var outcome = framework.ProcessTransaction(intent, manifest);

        Assert.Equal(Decision.Escalate, outcome.PolicyDecision.Decision);
        Assert.Contains("HUMAN_APPROVAL_REQUIRED", outcome.PolicyDecision.ReasonCodes);

        // 4. The AI's evidence made it all the way into the audit record, so the escalation is
        // explainable from both sides: why the AI flagged it, and why policy independently agreed.
        var audit = framework.FindLatestAudit(candidate.TransactionId);
        Assert.NotNull(audit);
        Assert.Contains(audit!.Evidence.CitedEvidence, e => e.EvidenceId.StartsWith("device-history-"));
        Assert.Contains(audit.Evidence.CitedEvidence, e => e.EvidenceId.StartsWith("beneficiary-creation-"));
    }
}
