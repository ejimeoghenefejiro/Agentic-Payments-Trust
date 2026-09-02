using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Intelligence.Learning;

/// <summary>What actually happened, as determined later by a human or the real-world outcome —
/// "Agent: ESCALATE. Human: Legitimate. Store it." / "Agent: low risk. Human: Suspicious. Store
/// it." Ground truth for evaluating the AI's recommendations against.</summary>
public enum ActualOutcome
{
    Legitimate,
    Suspicious
}

public sealed record DecisionFeedback(
    string TransactionId,
    IntelligenceRecommendation AiRecommendation,
    ActualOutcome ActualOutcome,
    string? Notes,
    DateTimeOffset RecordedAt);
