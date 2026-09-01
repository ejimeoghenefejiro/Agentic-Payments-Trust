using System.Text.Json;
using AgentTrust.Agents;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Orchestration;
using AgentTrust.Payments;

namespace AgentTrust.Runner;

public sealed class ScenarioResult
{
    public string ScenarioId { get; set; } = "";
    public string Description { get; set; } = "";
    public string ExpectedDecision { get; set; } = "";
    public string ActualDecision { get; set; } = "";
    public bool Correct { get; set; }
    public bool AgentMode { get; set; }
    public string AgentOutputStatus { get; set; } = "";
    public double EvidencePrecision { get; set; }
    public double EvidenceRecall { get; set; }
    public double EvidenceF1 { get; set; }
    public string PaymentStatus { get; set; } = "";
    public long AgentLatencyMs { get; set; }
    public long PolicyLatencyMs { get; set; }
    public List<string> ReasonCodes { get; set; } = new();
}

public static class ScenarioRunner
{
    public static ScenarioDefinition[] LoadAll(string scenariosDir)
    {
        var files = Directory.GetFiles(scenariosDir, "*.json").OrderBy(f => f);
        return files
            .Select(f => JsonSerializer.Deserialize<ScenarioDefinition>(
                File.ReadAllText(f),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
            .ToArray();
    }

    public static Task<ScenarioResult> RunAsync(ScenarioDefinition scenario) =>
        string.IsNullOrWhiteSpace(scenario.UserInstruction)
            ? Task.FromResult(RunDirectInjection(scenario))
            : RunAgentMode(scenario);

    /// <summary>Baseline mode: the scenario supplies the TransactionIntent directly, isolating
    /// policy-engine correctness from agent-intent-generation correctness.</summary>
    private static ScenarioResult RunDirectInjection(ScenarioDefinition scenario)
    {
        var (framework, intent, evidenceManifest) = BuildDirectFixture(scenario);
        var outcome = framework.ProcessTransaction(intent, evidenceManifest);

        var actual = outcome.PolicyDecision.Decision.ToString();
        return new ScenarioResult
        {
            ScenarioId = scenario.ScenarioId,
            Description = scenario.Description,
            ExpectedDecision = scenario.ExpectedDecision,
            ActualDecision = actual,
            Correct = string.Equals(actual, scenario.ExpectedDecision, StringComparison.OrdinalIgnoreCase),
            AgentMode = false,
            AgentOutputStatus = "N/A",
            EvidencePrecision = evidenceManifest.Precision,
            EvidenceRecall = evidenceManifest.Recall,
            EvidenceF1 = evidenceManifest.F1,
            PaymentStatus = outcome.PaymentResult.Status.ToString(),
            PolicyLatencyMs = outcome.LatencyMs,
            ReasonCodes = outcome.PolicyDecision.ReasonCodes.ToList()
        };
    }

    /// <summary>Agent mode: a real IPaymentAgent (Semantic Kernel) turns the natural-language
    /// instruction + evidence into a TransactionIntent, which is then validated and, if
    /// structurally sound, passed through the same trust framework as direct-injection mode.</summary>
    private static async Task<ScenarioResult> RunAgentMode(ScenarioDefinition scenario)
    {
        var (framework, agentId, principalId) = BuildAgentFixture(scenario);

        IPaymentAgent agent = scenario.ScriptedAgentResponse is not null
            ? AgentFactory.CreateScripted(agentId, scenario.ScriptedAgentResponse)
            : AgentFactory.IsLiveModeConfigured
                ? AgentFactory.CreateLive(agentId)
                : throw new InvalidOperationException(
                    $"Scenario {scenario.ScenarioId} needs either ScriptedAgentResponse or OPENAI_API_KEY to run in agent mode.");

        var availableEvidence = scenario.Evidence
            .Select(e => new EvidenceItem(e.EvidenceId, e.Type, e.Description, e.Exists))
            .ToList();

        var context = new AgentProposalContext(
            scenario.Intent.TransactionId,
            agentId,
            principalId,
            scenario.UserInstruction!,
            availableEvidence,
            scenario.Context,
            scenario.ExpectedCurrency,
            DateTimeOffset.Parse(scenario.Intent.RequestedAt));

        var proposal = await agent.ProposeTransactionAsync(context);

        if (proposal.Status == AgentOutputStatus.Invalid)
        {
            var decision = ClassifyInvalidOutput(proposal.ValidationReasonCodes);
            var shadowIntent = BuildShadowIntent(scenario, context, proposal.RawOutput);
            var citedEvidence = (proposal.RawOutput?.EvidenceIds ?? Array.Empty<string>())
                .Select(id => availableEvidence.FirstOrDefault(e => e.EvidenceId == id) ?? new EvidenceItem(id, "unknown", "fabricated reference", false))
                .ToList();
            var evidenceManifest = new EvidenceManifest(shadowIntent.TransactionId, citedEvidence, scenario.RequiredEvidenceTypes);

            var policyDecision = new PolicyDecisionResult(
                shadowIntent.TransactionId, decision,
                new[] { new PolicyCheck("AgentOutputValid", false, string.Join(",", proposal.ValidationReasonCodes)) },
                "agent-output-validation-v1", proposal.ValidationReasonCodes);
            var paymentResult = new PaymentResult(shadowIntent.TransactionId, PaymentStatus.NotAttempted, string.Empty, null);
            var audit = new AgentTrust.Evidence.EvidenceService().BuildAuditRecord(
                shadowIntent, scenario.Authority?.AuthorityId ?? "unknown", evidenceManifest, policyDecision, paymentResult, DateTimeOffset.UtcNow);
            framework.AuditLedger.Append(audit);

            return new ScenarioResult
            {
                ScenarioId = scenario.ScenarioId,
                Description = scenario.Description,
                ExpectedDecision = scenario.ExpectedDecision,
                ActualDecision = decision.ToString(),
                Correct = string.Equals(decision.ToString(), scenario.ExpectedDecision, StringComparison.OrdinalIgnoreCase),
                AgentMode = true,
                AgentOutputStatus = "Invalid",
                EvidencePrecision = evidenceManifest.Precision,
                EvidenceRecall = evidenceManifest.Recall,
                EvidenceF1 = evidenceManifest.F1,
                PaymentStatus = paymentResult.Status.ToString(),
                AgentLatencyMs = proposal.AgentLatencyMs,
                ReasonCodes = proposal.ValidationReasonCodes.ToList()
            };
        }

        var manifest = new EvidenceManifest(proposal.Intent!.TransactionId, proposal.Intent.Evidence.ToList(), scenario.RequiredEvidenceTypes);
        var outcome = framework.ProcessTransaction(proposal.Intent, manifest);
        var actual = outcome.PolicyDecision.Decision.ToString();

        return new ScenarioResult
        {
            ScenarioId = scenario.ScenarioId,
            Description = scenario.Description,
            ExpectedDecision = scenario.ExpectedDecision,
            ActualDecision = actual,
            Correct = string.Equals(actual, scenario.ExpectedDecision, StringComparison.OrdinalIgnoreCase),
            AgentMode = true,
            AgentOutputStatus = "Valid",
            EvidencePrecision = manifest.Precision,
            EvidenceRecall = manifest.Recall,
            EvidenceF1 = manifest.F1,
            PaymentStatus = outcome.PaymentResult.Status.ToString(),
            AgentLatencyMs = proposal.AgentLatencyMs,
            PolicyLatencyMs = outcome.LatencyMs,
            ReasonCodes = outcome.PolicyDecision.ReasonCodes.ToList()
        };
    }

    private static Decision ClassifyInvalidOutput(IReadOnlyList<string> reasonCodes)
    {
        var hardFailures = new[] { "INVALID_AGENT_OUTPUT", "MISSING_TRANSACTION_AMOUNT" };
        return reasonCodes.Any(hardFailures.Contains) ? Decision.Deny : Decision.Escalate;
    }

    private static TransactionIntent BuildShadowIntent(ScenarioDefinition scenario, AgentProposalContext context, RawAgentOutput? raw) => new(
        context.TransactionId,
        context.AgentId,
        context.PrincipalId,
        raw?.Action ?? "unknown",
        raw?.Merchant ?? "unknown",
        raw?.Category ?? "",
        raw?.Amount ?? 0,
        raw?.Reason ?? "invalid_agent_output",
        Array.Empty<EvidenceItem>(),
        context.OccurredAt,
        context.TransactionId);

    private static (TrustFramework Framework, TransactionIntent Intent, EvidenceManifest Evidence) BuildDirectFixture(ScenarioDefinition scenario)
    {
        var (framework, _, _) = BuildAgentFixture(scenario);

        var intent = new TransactionIntent(
            scenario.Intent.TransactionId,
            scenario.Identity.AgentId,
            scenario.Identity.PrincipalId,
            scenario.Intent.Action,
            scenario.Intent.Merchant,
            scenario.Intent.Category,
            scenario.Intent.Amount,
            scenario.Intent.Reason,
            scenario.Evidence.Select(e => new EvidenceItem(e.EvidenceId, e.Type, e.Description, e.Exists)).ToList(),
            DateTimeOffset.Parse(scenario.Intent.RequestedAt),
            scenario.Intent.IdempotencyKey);

        if (scenario.SimulatePriorApprovedDuplicate)
        {
            var priorTxId = $"{scenario.Intent.TransactionId}-original";
            var priorIntent = intent with { TransactionId = priorTxId };
            ((InMemoryTransactionLedger)framework.Ledger).Record(priorIntent, Decision.Approve);
        }

        var evidenceManifest = new EvidenceManifest(
            scenario.Intent.TransactionId,
            intent.Evidence.ToList(),
            scenario.RequiredEvidenceTypes);

        return (framework, intent, evidenceManifest);
    }

    private static (TrustFramework Framework, string AgentId, string PrincipalId) BuildAgentFixture(ScenarioDefinition scenario)
    {
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        var ledger = new InMemoryTransactionLedger();
        var paymentAdapter = new MockPaymentAdapter();

        var identity = new AgentIdentity(
            scenario.Identity.AgentId,
            scenario.Identity.PrincipalId,
            scenario.Identity.AgentType,
            scenario.Identity.Environment,
            Enum.Parse<CredentialStatus>(scenario.Identity.CredentialStatus),
            DateTimeOffset.Parse(scenario.Identity.IssuedAt),
            DateTimeOffset.Parse(scenario.Identity.ExpiresAt),
            scenario.Identity.Issuer);
        agents.Register(identity);

        if (scenario.Binding is not null)
        {
            bindings.Bind(new PrincipalBinding(
                scenario.Identity.AgentId,
                scenario.Identity.PrincipalId,
                DateTimeOffset.Parse(scenario.Identity.IssuedAt),
                scenario.Binding.Active,
                scenario.Binding.BindingEvidenceRef));
        }

        if (scenario.Authority is not null && !scenario.Authority.Missing)
        {
            authorities.Grant(new DelegatedAuthority(
                scenario.Authority.AuthorityId,
                scenario.Identity.AgentId,
                scenario.Authority.Permissions,
                scenario.Authority.PerTransactionLimit,
                scenario.Authority.DailyLimit,
                scenario.Authority.ApprovedMerchants,
                scenario.Authority.CategoryScope,
                scenario.Authority.GeographicScope,
                scenario.Authority.WindowStart is null ? null : TimeOnly.Parse(scenario.Authority.WindowStart),
                scenario.Authority.WindowEnd is null ? null : TimeOnly.Parse(scenario.Authority.WindowEnd),
                scenario.Authority.HumanApprovalAbove,
                DateOnly.Parse(scenario.Authority.Expiry),
                scenario.Authority.Revoked));
        }

        if (scenario.PreExistingDailySpend > 0)
        {
            var priorTxId = $"{scenario.Intent.TransactionId}-prior";
            var priorIntent = new TransactionIntent(
                priorTxId, scenario.Identity.AgentId, scenario.Identity.PrincipalId,
                scenario.Intent.Action, scenario.Intent.Merchant, scenario.Intent.Category,
                scenario.PreExistingDailySpend, "prior_spend", Array.Empty<EvidenceItem>(),
                DateTimeOffset.Parse(scenario.Intent.RequestedAt), null);
            ledger.Record(priorIntent, Decision.Approve);
        }

        if (scenario.ForcePaymentFailure)
        {
            paymentAdapter.ForcedFailures.Add(scenario.Intent.TransactionId);
        }

        var framework = new TrustFramework(agents, bindings, authorities, ledger, paymentAdapter);
        return (framework, scenario.Identity.AgentId, scenario.Identity.PrincipalId);
    }
}
