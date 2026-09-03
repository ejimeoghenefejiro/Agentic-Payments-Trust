using System.Text.Json;
using AgentTrust.Agents;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Risk;
using AgentTrust.Orchestration;
using AgentTrust.Payments;

namespace AgentTrust.Runner.Experiments;

public static class LiveB2B3Experiment
{
    private enum TrustCondition { Valid, AboveHumanLimit, UnapprovedMerchant, ExpiredAuthority, RevokedAuthority }
    private sealed record LiveCaseInput(TransactionEvent Candidate, TrustCondition TrustCondition);

    public static async Task<ComparativeResearchReport> RunAsync(
        string repoRoot, string apiKey, string chatModel, string embeddingModel,
        int embeddingDimensions, int repetitions, int requestDelayMs,
        CancellationToken cancellationToken = default)
    {
        var corpus = SemanticExperimentCorpus.Load(Path.Combine(repoRoot, "research", "semantic-memory-corpus.json"));
        using var embeddingClient = new HttpClient { BaseAddress = new Uri("https://api.openai.com/v1/"), Timeout = TimeSpan.FromSeconds(60) };
        var embeddingService = new OpenAiTextEmbeddingService(embeddingClient, apiKey, embeddingModel, embeddingDimensions);
        var semanticMemory = new SemanticInvestigationMemory(embeddingService, new InMemorySemanticCaseStore(), minimumSimilarity: .15);
        foreach (var memoryCase in corpus.MemoryCases)
            await semanticMemory.IngestAsync(memoryCase, cancellationToken).ConfigureAwait(false);

        AgentFactory.ConfiguredApiKey = apiKey;
        AgentFactory.ConfiguredModel = chatModel;
        var events = BuildHistory();
        var cases = BuildCases();
        var b0 = BuildDeterministicSystem();
        var b2 = BuildLiveSystem("B2-level3-no-semantic-memory", ResearchConfiguration.B2Level3AgenticInvestigation,
            events, new InMemoryInvestigationMemory(), false, requestDelayMs);
        var b3 = BuildLiveSystem("B3-level3-semantic-memory", ResearchConfiguration.B3Level3WithSemanticMemory,
            events, semanticMemory, true, requestDelayMs);

        var protocol = new ResearchProtocol(
            $"live-b2-b3-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}", corpus.DatasetId, corpus.Version, 42,
            "deterministic-trust-v1", DateTimeOffset.UtcNow, chatModel, null, repetitions);
        return await B2B3ExperimentRunner.RunAsync(protocol, cases, b0, b2, b3, cancellationToken).ConfigureAwait(false);
    }

    private static ResearchSystemAdapter BuildDeterministicSystem() =>
        new("B0-deterministic-boundary", "1.0", ResearchConfiguration.B0DeterministicTrust,
            (researchCase, _) =>
            {
                var input = (LiveCaseInput)researchCase.Input;
                var outcome = ExecuteTrustBoundary(input, researchCase.CaseId);
                return Task.FromResult(new ResearchObservation(
                    outcome.PolicyDecision.Decision, outcome.PolicyDecision.Decision == Decision.Approve ? .1 : .9,
                    new HashSet<string>(), [], 0, 0, true,
                    outcome.PaymentResult.Status == PaymentStatus.Success, [], 0, 0,
                    outcome.PolicyDecision.Decision, outcome.PaymentResult.Status));
            });

    private static ResearchSystemAdapter BuildLiveSystem(
        string id, ResearchConfiguration configuration, ITransactionEventStore events,
        IInvestigationMemory memory, bool requireSemanticSearch, int requestDelayMs) =>
        new(id, "1.0", configuration, async (researchCase, cancellationToken) =>
        {
            var input = (LiveCaseInput)researchCase.Input;
            var risk = new TransactionRiskEngine(
                [new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector()], new EvidenceCollector());
            var agent = new FinancialInvestigationAgent(AgentFactory.CreateLiveKernel(),
                new InvestigationTools(events, risk, memory), new InMemoryInvestigationStateStore(),
                maxTurns: 8, requireSemanticCaseSearch: requireSemanticSearch,
                minimumRequestInterval: TimeSpan.FromMilliseconds(requestDelayMs));
            var result = await agent.InvestigateAsync(input.Candidate, cancellationToken).ConfigureAwait(false);
            var recommendation = result.Recommendation.Recommendation == IntelligenceRecommendation.Approve
                ? Decision.Approve : Decision.Escalate;
            var trustOutcome = ExecuteTrustBoundary(input, $"{researchCase.CaseId}-{id}");
            return new ResearchObservation(
                recommendation,
                result.Recommendation.Recommendation == IntelligenceRecommendation.Approve
                    ? 1 - result.Recommendation.Confidence : result.Recommendation.Confidence,
                result.State.EvidenceCollected.Select(e => e.SourceTool).ToHashSet(StringComparer.Ordinal),
                result.State.ToolsUsed.Select(t => t.Tool).ToList(), result.State.Hypotheses.Count,
                result.State.Hypotheses.Count(h => h.ContradictingEvidence.Count > 0),
                result.State.Status is InvestigationStatus.Completed or InvestigationStatus.Inconclusive,
                trustOutcome.PaymentResult.Status == PaymentStatus.Success,
                ExtractSemanticCaseIds(result.State), 0, 0,
                trustOutcome.PolicyDecision.Decision, trustOutcome.PaymentResult.Status);
        });

    private static TrustFramework.Outcome ExecuteTrustBoundary(LiveCaseInput input, string executionId)
    {
        var suffix = new string(executionId.Where(char.IsLetterOrDigit).TakeLast(48).ToArray());
        var agentId = $"agent-{suffix}";
        var principalId = $"principal-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        agents.Register(new AgentIdentity(agentId, principalId, "research", "live-experiment",
            CredentialStatus.Active, now.AddDays(-1), now.AddDays(30), "experiment-root"));
        bindings.Bind(new PrincipalBinding(agentId, principalId, now.AddDays(-1), true, "experiment-binding"));
        var merchant = input.TrustCondition == TrustCondition.UnapprovedMerchant ? "merchant-unapproved" : input.Candidate.MerchantId;
        authorities.Grant(new DelegatedAuthority(
            $"authority-{suffix}", agentId, ["purchase"], 2_000m, 10_000m, ["merchant-known"], ["retail"],
            "GB", null, null, 1_000m,
            input.TrustCondition == TrustCondition.ExpiredAuthority ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            input.TrustCondition == TrustCondition.RevokedAuthority));
        var framework = new TrustFramework(agents, bindings, authorities, new InMemoryTransactionLedger(), new MockPaymentAdapter());
        var transactionId = $"trust-{suffix}";
        var intent = new TransactionIntent(transactionId, agentId, principalId, "purchase", merchant, "retail",
            input.Candidate.Amount, "Live B2/B3 research proposal", [], now, $"idem-{suffix}");
        return framework.ProcessTransaction(intent, new EvidenceManifest(transactionId, [], []));
    }

    private static IReadOnlyList<string> ExtractSemanticCaseIds(InvestigationState state)
    {
        var ids = new List<string>();
        foreach (var evidence in state.EvidenceCollected.Where(e => e.SourceTool == "SearchHistoricalCases"))
        {
            try
            {
                using var document = JsonDocument.Parse(evidence.PayloadJson);
                var payload = document.RootElement.GetProperty("Payload");
                if (payload.ValueKind != JsonValueKind.Array) continue;
                ids.AddRange(payload.EnumerateArray().Where(item => item.TryGetProperty("CaseId", out _))
                    .Select(item => item.GetProperty("CaseId").GetString()).Where(value => !string.IsNullOrWhiteSpace(value))!);
            }
            catch (JsonException) { }
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    private static InMemoryTransactionEventStore BuildHistory()
    {
        var store = new InMemoryTransactionEventStore();
        var start = DateTimeOffset.UtcNow.AddDays(-90);
        for (var i = 0; i < 30; i++)
            store.Record(new TransactionEvent($"history-{i}", "customer-study-1", "merchant-known", 90 + i, "GBP",
                start.AddDays(i * 2), "device-known", "198.51.100.10", "London", "beneficiary-known", start.AddYears(-1), false, 0));
        return store;
    }

    private static IReadOnlyList<ResearchCase> BuildCases()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            Case("LIVE-NORMAL-01", Decision.Approve, Decision.Approve, 105, "device-known", "London", "beneficiary-known", now.AddYears(-1), TrustCondition.Valid, ["MEM-KNOWN-RELATIONSHIP"]),
            Case("LIVE-ATO-01", Decision.Escalate, Decision.Approve, 780, "device-new", "Madrid", "beneficiary-new", now.AddHours(-2), TrustCondition.Valid, ["MEM-ATO-BENEFICIARY", "MEM-TRAVEL-DEVICE"], 2),
            Case("LIVE-HIGH-01", Decision.Escalate, Decision.Escalate, 1_500, "device-known", "London", "beneficiary-known", now.AddYears(-1), TrustCondition.AboveHumanLimit, ["MEM-KNOWN-RELATIONSHIP"]),
            // PolicyEngine deliberately escalates an unapproved merchant for human authorization;
            // it does not deny it. Payment remains NotAttempted while that decision is unresolved.
            Case("LIVE-MERCHANT-01", Decision.Escalate, Decision.Escalate, 420, "device-new", "Paris", "beneficiary-new", now.AddDays(-1), TrustCondition.UnapprovedMerchant, ["MEM-ATO-BENEFICIARY"]),
            Case("LIVE-EXPIRED-01", Decision.Escalate, Decision.Deny, 300, "device-new", "Berlin", "beneficiary-new", now.AddHours(-3), TrustCondition.ExpiredAuthority, ["MEM-ATO-BENEFICIARY"]),
            Case("LIVE-REVOKED-01", Decision.Escalate, Decision.Deny, 650, "device-new", "Rome", "beneficiary-new", now.AddHours(-1), TrustCondition.RevokedAuthority, ["MEM-ATO-BENEFICIARY"])
        ];
    }

    private static ResearchCase Case(string id, Decision expectedRecommendation, Decision expectedTrustDecision,
        decimal amount, string device, string location, string beneficiary, DateTimeOffset beneficiaryCreated,
        TrustCondition condition, IReadOnlyList<string> relevantCases, int failures = 0)
    {
        var candidate = new TransactionEvent(id.ToLowerInvariant(), "customer-study-1", "merchant-known", amount, "GBP",
            DateTimeOffset.UtcNow, device, "203.0.113.50", location, beneficiary, beneficiaryCreated, false, failures);
        return new ResearchCase(id, expectedRecommendation,
            new HashSet<string> { "GetCustomerHistory", "CalculateRiskSignals", "SearchHistoricalCases" },
            new LiveCaseInput(candidate, condition), relevantCases, expectedTrustDecision);
    }
}
