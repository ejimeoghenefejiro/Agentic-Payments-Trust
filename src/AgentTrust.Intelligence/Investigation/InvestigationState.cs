using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Intelligence.Investigation;

public enum InvestigationStatus { Investigating, Completed, Inconclusive, Failed }

public sealed record HypothesisState(
    string Id,
    string Description,
    IReadOnlyList<string> SupportingEvidence,
    IReadOnlyList<string> ContradictingEvidence,
    double Confidence);

public sealed record InvestigationEvidence(
    string EvidenceId,
    string SourceTool,
    string Summary,
    string PayloadJson,
    DateTimeOffset CollectedAt);

public sealed record InvestigationToolUse(
    int Turn,
    string Tool,
    IReadOnlyDictionary<string, string> Arguments,
    string Rationale,
    string ResultSummary);

public sealed record StructuredInvestigationRecommendation(
    IntelligenceRecommendation Recommendation,
    double Confidence,
    string Rationale,
    IReadOnlyList<string> KeyEvidence,
    IReadOnlyList<string> ContradictoryEvidence,
    string? RequiredAction,
    string Counterfactual);

public sealed class InvestigationState
{
    public required string InvestigationId { get; init; }
    public required string TransactionId { get; init; }
    public required string Objective { get; init; }
    public List<HypothesisState> Hypotheses { get; set; } = new();
    public List<string> OpenQuestions { get; set; } = new();
    public List<InvestigationToolUse> ToolsUsed { get; set; } = new();
    public List<InvestigationEvidence> EvidenceCollected { get; set; } = new();
    public InvestigationStatus Status { get; set; } = InvestigationStatus.Investigating;
    public int Turn { get; set; }
    public bool ConclusionChallenged { get; set; }
    public string? LatestReasoning { get; set; }
    public StructuredInvestigationRecommendation? Recommendation { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IInvestigationStateStore
{
    void Save(InvestigationState state);
    InvestigationState? Find(string investigationId);
}

public sealed class InMemoryInvestigationStateStore : IInvestigationStateStore
{
    private readonly Dictionary<string, InvestigationState> _states = new();
    public void Save(InvestigationState state) => _states[state.InvestigationId] = state;
    public InvestigationState? Find(string investigationId) => _states.GetValueOrDefault(investigationId);
}
