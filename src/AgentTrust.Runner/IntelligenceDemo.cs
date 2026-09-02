using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Risk;
using AgentTrust.Orchestration;
using AgentTrust.Payments;

namespace AgentTrust.Runner;

/// <summary>
/// Single-command demonstration of the "Financial Intelligence" layer described in the
/// long-term product vision: the AI's opinion (a risk-scored, evidence-backed recommendation)
/// feeding into the same, unmodified trust layer used everywhere else in this repo — the trust
/// layer decides independently, on its own deterministic grounds, and never trusts the risk
/// score itself. Reproduces the vision doc's worked example: customer C10391, normal £30-£400 /
/// Manchester-Salford / devices D44-D71 / beneficiaries B101-B201 / 07:00-23:00, then a 03:41
/// transaction for £8,700 to a brand-new beneficiary from a new device/location, with the
/// beneficiary added two minutes earlier and three failed attempts just before it.
/// </summary>
public static class IntelligenceDemo
{
    public static void Run()
    {
        Step("1. Customer behaviour profile is built from history", "40 prior transactions for C10391, varied but typical.");
        var eventStore = new InMemoryTransactionEventStore();
        var baseTime = new DateTimeOffset(2027, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var rng = new Random(7);
        for (var i = 0; i < 40; i++)
        {
            var amount = 30m + (decimal)rng.NextDouble() * 370m;
            var hour = 7 + rng.Next(0, 16);
            var device = i % 5 == 0 ? "D71" : "D44";
            var location = i % 4 == 0 ? "Salford" : "Manchester";
            var beneficiary = i % 3 == 0 ? "B201" : "B101";
            eventStore.Record(new TransactionEvent($"tx_hist_{i}", "C10391", "M14", amount, "GBP",
                baseTime.AddDays(i).AddHours(hour), device, "1.2.3.4", location, beneficiary, null, false, 0));
        }
        var profile = BehaviourProfileBuilder.BuildCustomerProfile("C10391", eventStore.GetCustomerHistory("C10391"));
        Print($"Typical amount range: {profile.TypicalMinAmount:C}-{profile.TypicalMaxAmount:C}");
        Print($"Typical devices: {string.Join(", ", profile.TypicalDevices)}; locations: {string.Join(", ", profile.TypicalLocations)}");
        Print($"Regular beneficiaries: {string.Join(", ", profile.RegularBeneficiaries)}");

        Step("2. A new candidate transaction arrives at 03:41", "£8,700 to a new beneficiary, new device, new location.");
        var candidate = new TransactionEvent(
            "tx_night_8700", "C10391", "M14", 8700m, "GBP",
            new DateTimeOffset(2027, 6, 25, 3, 41, 0, TimeSpan.Zero),
            "D999-unknown", "203.0.113.9", "Lagos",
            "B999-new", new DateTimeOffset(2027, 6, 25, 3, 39, 0, TimeSpan.Zero),
            false, 3);
        Print($"Amount: {candidate.Amount:C}  Time: {candidate.Timestamp.UtcDateTime:HH:mm}  Device: {candidate.DeviceId}  Location: {candidate.Location}");
        Print($"Beneficiary: {candidate.BeneficiaryId} (added {(candidate.Timestamp - candidate.BeneficiaryCreatedAt!.Value).TotalMinutes:F0} minutes ago)  Prior failed attempts: {candidate.PriorFailedAttempts}");

        Step("3. Financial intelligence layer investigates", "AI reasons over context, not just amount vs. a single threshold.");
        var riskEngine = new TransactionRiskEngine(
            new IAnomalyDetector[] { new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector() },
            new EvidenceCollector());
        var investigationAgent = new InvestigationAgent(eventStore, riskEngine);
        var assessment = investigationAgent.Investigate(candidate);
        Print($"Risk factors detected: {assessment.RiskFactors.Count}");
        foreach (var factor in assessment.RiskFactors)
        {
            Print($"  - {factor.Factor} (weight {factor.Weight:F2}): {factor.Detail}");
        }
        Print($"Risk score: {assessment.RiskScore}/100   Confidence: {assessment.Confidence:P0}");
        Print($"AI recommendation: {assessment.Recommendation}  <-- advisory only, does not authorise anything");

        Step("4. The AI's evidence is handed to the frozen, unmodified trust layer", "Same TrustFramework/PolicyEngine used throughout this repo.");
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
            1000m, DateOnly.Parse("2027-12-31"), false));

        var intent = new TransactionIntent(
            candidate.TransactionId, "agt_c10391", "org_c10391", "transfer:funds", candidate.MerchantId,
            "transfer", candidate.Amount, "AI-flagged: " + string.Join(", ", assessment.RiskFactors.Select(f => f.Factor)),
            assessment.EvidenceReferences, candidate.Timestamp, candidate.TransactionId);
        var manifest = new EvidenceManifest(candidate.TransactionId, assessment.EvidenceReferences.ToList(), Array.Empty<string>());
        var outcome = framework.ProcessTransaction(intent, manifest);

        Print($"Policy decision: {outcome.PolicyDecision.Decision}  Reason codes: {string.Join(", ", outcome.PolicyDecision.ReasonCodes)}");
        Print("Note: the policy engine never saw the risk score — it independently escalated on its own human-approval threshold (£1,000).");

        Console.WriteLine();
        Console.WriteLine("=== INTELLIGENCE DEMO RESULT ===");
        Console.WriteLine($"AI recommendation:     {assessment.Recommendation} (risk {assessment.RiskScore}/100)");
        Console.WriteLine($"Trust layer decision:  {outcome.PolicyDecision.Decision}");
        Console.WriteLine($"Independent agreement: {(assessment.Recommendation == IntelligenceRecommendation.Escalate) == (outcome.PolicyDecision.Decision == Decision.Escalate)}");
    }

    private static void Step(string title, string detail)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
        Console.WriteLine(detail);
    }

    private static void Print(string line) => Console.WriteLine($"  {line}");
}
