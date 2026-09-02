using System.Diagnostics;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Evidence;
using AgentTrust.Orchestration;
using AgentTrust.Payments;

namespace AgentTrust.Runner.Experiments;

/// <summary>
/// Executes a generated scenario set against one shared TrustFramework instance (shared audit
/// ledger, shared ledger/registry/store dictionaries — but every scenario carries its own unique
/// agent/principal/authority ids, so scenarios never interfere with each other's identity,
/// authority, or duplicate/daily-limit checks). Running everything through one framework, rather
/// than a fresh one per scenario, is what makes the audit-chain verification and audit
/// reconstruction metric meaningful at scale instead of trivially true for a chain of length one.
///
/// All stores are optional and default to in-memory — pass EF-Core-backed stores (and a
/// TrustFramework built from them) to run the exact same generated dataset against a real
/// database instead, exercising the persistence layer under this workload.
/// </summary>
public static class ExperimentRunner
{
    public static (IReadOnlyList<ExperimentResult> Results, AuditChainVerificationResult ChainVerification) Run(
        int seed,
        int count,
        IAgentRegistry? agents = null,
        IPrincipalBindingStore? bindings = null,
        IDelegatedAuthorityStore? authorities = null,
        MockPaymentAdapter? paymentAdapter = null,
        TrustFramework? framework = null)
    {
        agents ??= new InMemoryAgentRegistry();
        bindings ??= new InMemoryPrincipalBindingStore();
        authorities ??= new InMemoryDelegatedAuthorityStore();
        paymentAdapter ??= new MockPaymentAdapter();
        framework ??= new TrustFramework(agents, bindings, authorities, new InMemoryTransactionLedger(), paymentAdapter);

        var scenarios = ScenarioGenerator.Generate(seed, count);
        var results = new List<ExperimentResult>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            agents.Register(scenario.Identity);
            bindings.Bind(scenario.Binding);
            authorities.Grant(scenario.Authority);

            // Seed prior state by running real, always-approving transactions through the same
            // framework (not by poking a ledger's internals directly) so seeding is correct
            // regardless of whether the backing stores are in-memory or a real database.
            if (scenario.SeedPriorApprovedDuplicate)
            {
                var priorIntent = scenario.Intent with { TransactionId = scenario.Intent.TransactionId + "-original" };
                framework.ProcessTransaction(priorIntent, scenario.EvidenceManifest);
            }

            if (scenario.PreExistingDailySpend > 0)
            {
                SeedDailySpend(framework, scenario);
            }

            if (scenario.ForcePaymentFailure)
            {
                paymentAdapter.ForcedFailures.Add(scenario.Intent.TransactionId);
            }

            var stopwatch = Stopwatch.StartNew();
            var outcome = framework.ProcessTransaction(scenario.Intent, scenario.EvidenceManifest);
            stopwatch.Stop();

            var decisionCorrect = outcome.PolicyDecision.Decision == scenario.ExpectedDecision;
            var reasonCorrect = scenario.ExpectedReasonCode is null
                || outcome.PolicyDecision.ReasonCodes.Contains(scenario.ExpectedReasonCode);
            var paymentCorrect = outcome.PaymentResult.Status == scenario.ExpectedPaymentStatus;

            var audit = framework.FindLatestAudit(scenario.Intent.TransactionId);
            var reconstructable = audit is not null
                && audit.TransactionId == scenario.Intent.TransactionId
                && audit.PolicyDecision.Decision == outcome.PolicyDecision.Decision
                && audit.PaymentResult.Status == outcome.PaymentResult.Status;

            results.Add(new ExperimentResult(
                scenario.ScenarioId,
                scenario.Category,
                scenario.ExpectedDecision,
                outcome.PolicyDecision.Decision,
                decisionCorrect,
                scenario.ExpectedReasonCode,
                outcome.PolicyDecision.ReasonCodes,
                reasonCorrect,
                scenario.ExpectedPaymentStatus,
                outcome.PaymentResult.Status,
                paymentCorrect,
                scenario.EvidenceManifest.Precision,
                scenario.EvidenceManifest.Recall,
                scenario.EvidenceManifest.F1,
                outcome.LatencyMs,
                stopwatch.Elapsed.TotalMilliseconds,
                reconstructable));
        }

        var chainVerification = framework.AuditLedger.Verify();
        return (results, chainVerification);
    }

    /// <summary>Splits the seeded daily spend into chunks that stay under both the
    /// per-transaction limit AND the human-approval threshold, so every seed chunk is a real
    /// Approve (not an Escalate, which the ledger never counts as spend) regardless of backend.</summary>
    private static void SeedDailySpend(TrustFramework framework, GeneratedScenario scenario)
    {
        var remaining = scenario.PreExistingDailySpend;
        var chunkSize = Math.Min(scenario.Authority.PerTransactionLimit, scenario.Authority.HumanApprovalAbove) - 1m;
        var chunkIndex = 0;

        while (remaining > 0)
        {
            var amount = Math.Min(remaining, chunkSize);
            var chunkIntent = scenario.Intent with
            {
                TransactionId = $"{scenario.Intent.TransactionId}-prior-{chunkIndex}",
                Amount = amount,
                IdempotencyKey = null
            };
            framework.ProcessTransaction(chunkIntent, scenario.EvidenceManifest);
            remaining -= amount;
            chunkIndex++;
        }
    }
}
