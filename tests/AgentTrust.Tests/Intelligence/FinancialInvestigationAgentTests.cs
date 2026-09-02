using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Risk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class FinancialInvestigationAgentTests
{
    [Fact]
    public async Task AgentSelectsToolsMaintainsHypothesesChallengesAndCompletes()
    {
        var responses = new[]
        {
            """{"action":"use_tool","tool":"GetDeviceHistory","arguments":{"deviceId":"new-device"},"rationale":"Test account takeover by checking whether the device is genuinely new.","hypotheses":[{"id":"H1","description":"Account takeover","supportingEvidence":["new device"],"contradictingEvidence":[],"confidence":0.55},{"id":"H2","description":"Legitimate unusual purchase","supportingEvidence":[],"contradictingEvidence":["new device"],"confidence":0.3}],"openQuestions":["Has this device been used by another customer?"],"recommendation":null}""",
            """{"action":"use_tool","tool":"GetBeneficiaryHistory","arguments":{"beneficiaryId":"beneficiary-new"},"rationale":"Test whether the new beneficiary has suspicious prior relationships.","hypotheses":[{"id":"H1","description":"Account takeover","supportingEvidence":["new device","new beneficiary"],"contradictingEvidence":[],"confidence":0.7},{"id":"H2","description":"Legitimate unusual purchase","supportingEvidence":[],"contradictingEvidence":["new device"],"confidence":0.2}],"openQuestions":["Is there a legitimate explanation for the location?"],"recommendation":null}""",
            """{"action":"challenge","tool":null,"arguments":{},"rationale":"Challenge account takeover: travel or a device replacement could explain the location and device changes.","hypotheses":[{"id":"H1","description":"Account takeover","supportingEvidence":["new device","new beneficiary"],"contradictingEvidence":["travel not yet checked"],"confidence":0.62},{"id":"H2","description":"Legitimate unusual purchase","supportingEvidence":["possible travel"],"contradictingEvidence":["new beneficiary"],"confidence":0.3}],"openQuestions":[],"recommendation":null}""",
            """{"action":"complete","tool":null,"arguments":{},"rationale":"Material takeover indicators remain after considering the alternative.","hypotheses":[{"id":"H1","description":"Account takeover","supportingEvidence":["new device","new beneficiary"],"contradictingEvidence":["possible travel"],"confidence":0.68},{"id":"H2","description":"Legitimate unusual purchase","supportingEvidence":["possible travel"],"contradictingEvidence":["new beneficiary"],"confidence":0.3}],"openQuestions":[],"recommendation":{"recommendation":"Escalate","confidence":0.68,"rationale":"New device and beneficiary remain unexplained; verify the customer.","keyEvidence":["new device","new beneficiary"],"contradictoryEvidence":["possible travel"],"requiredAction":"Strong customer verification","counterfactual":"Verified travel and beneficiary ownership would weaken the takeover hypothesis."}}"""
        };
        var store = new InMemoryTransactionEventStore();
        store.Record(Event("old", "known-device", "known-beneficiary", DateTimeOffset.Parse("2027-01-01T12:00:00Z")));
        var states = new InMemoryInvestigationStateStore();
        var agent = BuildAgent(responses, store, states);

        var result = await agent.InvestigateAsync(Event("candidate", "new-device", "beneficiary-new", DateTimeOffset.Parse("2027-02-01T02:00:00Z")));

        Assert.Equal(InvestigationStatus.Completed, result.State.Status);
        Assert.True(result.State.ConclusionChallenged);
        Assert.Equal(2, result.State.ToolsUsed.Count);
        Assert.Equal(new[] { "GetDeviceHistory", "GetBeneficiaryHistory" }, result.State.ToolsUsed.Select(t => t.Tool));
        Assert.Equal(2, result.State.EvidenceCollected.Count);
        Assert.Equal(IntelligenceRecommendation.Escalate, result.Recommendation.Recommendation);
        Assert.NotNull(states.Find(result.State.InvestigationId));
    }

    [Fact]
    public async Task AgentCannotCallPaymentOrTrustLayerTools()
    {
        var response = """{"action":"use_tool","tool":"SubmitPayment","arguments":{},"rationale":"attempt","hypotheses":[],"openQuestions":[],"recommendation":null}""";
        var agent = BuildAgent(new[] { response }, new InMemoryTransactionEventStore(), new InMemoryInvestigationStateStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvestigateAsync(
            Event("candidate", "device", "beneficiary", DateTimeOffset.UtcNow)));

        Assert.Contains("forbidden tool", error.Message);
    }

    [Fact]
    public async Task AgentDefersCompletionUntilConclusionHasBeenChallenged()
    {
        var complete = """{"action":"complete","tool":null,"arguments":{},"rationale":"done","hypotheses":[],"openQuestions":[],"recommendation":{"recommendation":"Approve","confidence":0.8,"rationale":"normal","keyEvidence":[],"contradictoryEvidence":[],"requiredAction":null,"counterfactual":"new adverse evidence"}}""";
        var challenge = """{"action":"challenge","tool":null,"arguments":{},"rationale":"considered alternative","hypotheses":[],"openQuestions":[],"recommendation":null}""";
        var agent = BuildAgent(new[] { complete, challenge, complete }, new InMemoryTransactionEventStore(), new InMemoryInvestigationStateStore());

        var result = await agent.InvestigateAsync(Event("candidate", "device", "beneficiary", DateTimeOffset.UtcNow));

        Assert.True(result.State.ConclusionChallenged);
        Assert.Equal(3, result.State.Turn);
        Assert.Equal(InvestigationStatus.Completed, result.State.Status);
    }

    private static FinancialInvestigationAgent BuildAgent(IEnumerable<string> responses, ITransactionEventStore events, IInvestigationStateStore states)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(new QueuedChatCompletionService(responses));
        var risk = new TransactionRiskEngine(new IAnomalyDetector[] { new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector() }, new EvidenceCollector());
        return new FinancialInvestigationAgent(builder.Build(), new InvestigationTools(events, risk), states, maxTurns: 6);
    }

    private static TransactionEvent Event(string id, string device, string beneficiary, DateTimeOffset timestamp) =>
        new(id, "customer-1", "merchant-1", 900m, "GBP", timestamp, device, "1.2.3.4", "ES", beneficiary, timestamp.AddMinutes(-5), false, 0);

    private sealed class QueuedChatCompletionService : IChatCompletionService
    {
        private readonly Queue<string> _responses = new();
        public QueuedChatCompletionService(IEnumerable<string> responses) { foreach (var response in responses) _responses.Enqueue(response); }
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();
        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        {
            if (!_responses.TryDequeue(out var response)) throw new InvalidOperationException("No scripted reasoning response remains.");
            IReadOnlyList<ChatMessageContent> result = new[] { new ChatMessageContent(AuthorRole.Assistant, response) };
            return Task.FromResult(result);
        }
        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }
}
