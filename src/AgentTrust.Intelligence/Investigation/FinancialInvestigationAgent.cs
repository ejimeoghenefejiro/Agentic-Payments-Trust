using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Risk;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentTrust.Intelligence.Investigation;

public sealed record InvestigationRunResult(InvestigationState State, StructuredInvestigationRecommendation Recommendation);

internal sealed record ReasoningTurn(
    string Action,
    string? Tool,
    Dictionary<string, string>? Arguments,
    string Rationale,
    List<HypothesisState>? Hypotheses,
    List<string>? OpenQuestions,
    StructuredInvestigationRecommendation? Recommendation);

/// <summary>
/// Level-3 financial investigator. The model selects only read/analysis tools, iteratively updates
/// explicit hypotheses, must challenge its conclusion, and returns a recommendation. It has no
/// dependency on TrustFramework, PolicyEngine, delegated authority, approvals, or payment APIs.
/// </summary>
public sealed class FinancialInvestigationAgent
{
    private readonly Kernel _kernel;
    private readonly InvestigationTools _tools;
    private readonly IInvestigationStateStore _states;
    private readonly int _maxTurns;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FinancialInvestigationAgent(Kernel kernel, InvestigationTools tools, IInvestigationStateStore states, int maxTurns = 12)
    {
        if (maxTurns < 3) throw new ArgumentOutOfRangeException(nameof(maxTurns), "At least three turns are required for investigation and challenge.");
        _kernel = kernel;
        _tools = tools;
        _states = states;
        _maxTurns = maxTurns;
    }

    public async Task<InvestigationRunResult> InvestigateAsync(TransactionEvent candidate, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new InvestigationState
        {
            InvestigationId = $"inv_{Guid.NewGuid():N}", TransactionId = candidate.TransactionId,
            Objective = $"Determine whether transaction {candidate.TransactionId} is safe and what verification, if any, is required",
            CreatedAt = now, UpdatedAt = now
        };
        _states.Save(state);

        try
        {
            for (var turnNumber = 1; turnNumber <= _maxTurns; turnNumber++)
            {
                state.Turn = turnNumber;
                var turn = await RequestTurn(candidate, state, cancellationToken);
                ApplyReasoningState(state, turn);

                if (turn.Action.Equals("challenge", StringComparison.OrdinalIgnoreCase))
                {
                    state.ConclusionChallenged = true;
                    state.LatestReasoning = turn.Rationale;
                    Save(state);
                    continue;
                }

                if (turn.Action.Equals("complete", StringComparison.OrdinalIgnoreCase))
                {
                    if (!state.ConclusionChallenged)
                    {
                        state.OpenQuestions.Add("Challenge the leading conclusion and search for contradictory evidence before completing.");
                        state.LatestReasoning = "Completion deferred: the conclusion has not been challenged.";
                        Save(state);
                        continue;
                    }
                    if (turn.Recommendation is null) throw new InvalidOperationException("A completed investigation must include a structured recommendation.");
                    Complete(state, turn.Recommendation);
                    return new InvestigationRunResult(state, turn.Recommendation);
                }

                if (!turn.Action.Equals("use_tool", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(turn.Tool))
                    throw new InvalidOperationException("Reasoner must choose use_tool, challenge, or complete.");
                if (!InvestigationTools.AllowedToolNames.Contains(turn.Tool))
                    throw new InvalidOperationException($"Reasoner requested forbidden tool '{turn.Tool}'.");

                var arguments = (IReadOnlyDictionary<string, string>?)turn.Arguments ?? new Dictionary<string, string>();
                if (state.ToolsUsed.Any(t => t.Tool.Equals(turn.Tool, StringComparison.OrdinalIgnoreCase)
                    && ArgumentsEqual(t.Arguments, arguments)))
                {
                    state.OpenQuestions.Add($"Choose a different source; {turn.Tool} has already been called with the same arguments.");
                    state.LatestReasoning = "Duplicate tool call rejected by stop-control logic.";
                    Save(state);
                    continue;
                }

                var payload = _tools.Execute(turn.Tool, arguments, candidate);
                var summary = SummarizePayload(payload);
                state.ToolsUsed.Add(new InvestigationToolUse(turnNumber, turn.Tool, arguments, turn.Rationale, summary));
                state.EvidenceCollected.Add(new InvestigationEvidence(
                    $"iev_{state.InvestigationId}_{turnNumber}", turn.Tool, summary, payload, DateTimeOffset.UtcNow));
                state.LatestReasoning = turn.Rationale;
                Save(state);
            }

            var boundedStop = new StructuredInvestigationRecommendation(
                IntelligenceRecommendation.Escalate, 0,
                $"Investigation reached its {_maxTurns}-turn safety limit without sufficient evidence for a completed conclusion.",
                state.EvidenceCollected.Select(e => e.EvidenceId).ToList(), Array.Empty<string>(),
                "Human review required", "Additional verified evidence could change this recommendation.");
            state.Status = InvestigationStatus.Inconclusive;
            state.Recommendation = boundedStop;
            Save(state);
            return new InvestigationRunResult(state, boundedStop);
        }
        catch
        {
            state.Status = InvestigationStatus.Failed;
            Save(state);
            throw;
        }
    }

    private async Task<ReasoningTurn> RequestTurn(TransactionEvent candidate, InvestigationState state, CancellationToken cancellationToken)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage($"CANDIDATE:\n{JsonSerializer.Serialize(candidate, JsonOptions)}\n\nINVESTIGATION_STATE:\n{JsonSerializer.Serialize(state, JsonOptions)}");
        var response = await chat.GetChatMessageContentAsync(history, kernel: _kernel, cancellationToken: cancellationToken);
        return ParseTurn(response.Content);
    }

    private static void ApplyReasoningState(InvestigationState state, ReasoningTurn turn)
    {
        if (turn.Hypotheses is not null)
        {
            foreach (var hypothesis in turn.Hypotheses)
                if (hypothesis.Confidence is < 0 or > 1) throw new InvalidOperationException("Hypothesis confidence must be between 0 and 1.");
            state.Hypotheses = turn.Hypotheses;
        }
        if (turn.OpenQuestions is not null) state.OpenQuestions = turn.OpenQuestions.Distinct().ToList();
    }

    private static ReasoningTurn ParseTurn(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Reasoner returned an empty response.");
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end < start) throw new InvalidOperationException("Reasoner did not return a JSON object.");
        return JsonSerializer.Deserialize<ReasoningTurn>(content[start..(end + 1)], JsonOptions)
            ?? throw new InvalidOperationException("Reasoner response could not be parsed.");
    }

    private static bool ArgumentsEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(kv => right.TryGetValue(kv.Key, out var value) && value == kv.Value);
    private static string SummarizePayload(string payload) => payload.Length <= 500 ? payload : payload[..500] + "…";
    private void Save(InvestigationState state) { state.UpdatedAt = DateTimeOffset.UtcNow; _states.Save(state); }
    private void Complete(InvestigationState state, StructuredInvestigationRecommendation recommendation)
    {
        state.Status = InvestigationStatus.Completed; state.Recommendation = recommendation; Save(state);
    }

    private const string SystemPrompt = """
You are a financial investigation reasoner. You investigate; you never authorise, deny, approve,
execute, or move money. Maintain explicit competing hypotheses and evidence for and against each.
Choose one allowed analytical tool per turn, inspect its returned evidence on the next turn, and
seek contradictory evidence. Before completion you MUST issue a challenge action at least once.
Stop when material open questions are resolved or further tools cannot change the recommendation.

Allowed tools: GetCustomerHistory, GetMerchantHistory, GetDeviceHistory, GetBeneficiaryHistory,
CalculateBehaviourProfile, DetectAnomalies, AnalyseFinancialGraph, ComparePeerGroup,
GetPreviousHumanReviews, SearchHistoricalCases, RetrieveEvidence, CalculateRiskSignals.

Return JSON only. Shape:
{
  "action":"use_tool|challenge|complete",
  "tool":"AllowedToolName or null",
  "arguments":{"argument":"value"},
  "rationale":"what hypothesis or counter-hypothesis this turn tests",
  "hypotheses":[{"id":"H1","description":"...","supportingEvidence":[],"contradictingEvidence":[],"confidence":0.42}],
  "openQuestions":["..."],
  "recommendation":null
}
For complete, recommendation must contain recommendation (Approve or Escalate), confidence 0..1,
rationale, keyEvidence, contradictoryEvidence, requiredAction, and counterfactual. Recommendation is
advisory and cannot invoke the deterministic trust layer. Prefer Escalate when evidence is insufficient.
""";
}
