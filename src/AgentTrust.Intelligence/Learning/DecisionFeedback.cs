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

public enum OutcomeSource { HumanReview, CustomerConfirmation, Chargeback, ProviderDispute, Investigation }
public enum OutcomeValidationStatus { Pending, Validated, Rejected, Superseded }

public sealed record DecisionFeedback(
    string TransactionId,
    IntelligenceRecommendation AiRecommendation,
    ActualOutcome ActualOutcome,
    string? Notes,
    DateTimeOffset RecordedAt,
    string? InvestigationId = null,
    double? AgentConfidence = null,
    double? HumanConfidence = null,
    IReadOnlyList<string>? ReasonCodes = null,
    IReadOnlyList<string>? UsefulEvidenceIds = null,
    IReadOnlyList<string>? MisleadingEvidenceIds = null,
    OutcomeSource Source = OutcomeSource.HumanReview,
    OutcomeValidationStatus ValidationStatus = OutcomeValidationStatus.Pending,
    string? ValidatedBy = null,
    DateTimeOffset? ValidatedAt = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TransactionId);
        ValidateConfidence(AgentConfidence, nameof(AgentConfidence));
        ValidateConfidence(HumanConfidence, nameof(HumanConfidence));
        if (ValidationStatus == OutcomeValidationStatus.Validated &&
            (string.IsNullOrWhiteSpace(ValidatedBy) || ValidatedAt is null))
            throw new ArgumentException("Validated outcomes require validator identity and timestamp.");
        if (ValidatedAt is { } timestamp && timestamp < RecordedAt)
            throw new ArgumentException("Validation cannot predate feedback recording.");
    }

    private static void ValidateConfidence(double? value, string name)
    {
        if (value is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            throw new ArgumentOutOfRangeException(name, "Confidence must be between zero and one.");
    }
}
