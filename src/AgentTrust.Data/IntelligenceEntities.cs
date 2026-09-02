namespace AgentTrust.Data;

// Persistence for AgentTrust.Intelligence: raw transaction events (the source data both
// FinancialGraph and CustomerBehaviourProfile are built from on demand — the graph itself is
// never stored, only rebuilt from these rows) and periodic profile snapshots (long-term memory
// for behavioural-change detection).

public sealed class TransactionEventEntity
{
    public string TransactionId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string DeviceId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Location { get; set; } = "";
    public string? BeneficiaryId { get; set; }
    public DateTimeOffset? BeneficiaryCreatedAt { get; set; }
    public bool WasRefunded { get; set; }
    public int PriorFailedAttempts { get; set; }
}

public sealed class ProfileSnapshotEntity
{
    public int Id { get; set; }
    public string EntityId { get; set; } = "";
    public DateTimeOffset TakenAt { get; set; }
    public string ProfileJson { get; set; } = "";
}

public sealed class InvestigationStateEntity
{
    public string InvestigationId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string Status { get; set; } = "";
    public string StateJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SemanticCaseEntity
{
    public string CaseId { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string TagsJson { get; set; } = "[]";
    public string EmbeddingJson { get; set; } = "[]";
    public string EmbeddingProvider { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";
    public string? EmbeddingModelVersion { get; set; }
    public int EmbeddingDimensions { get; set; }
    public DateTimeOffset EmbeddingCreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DecisionFeedbackEntity
{
    public string TransactionId { get; set; } = "";
    public string? InvestigationId { get; set; }
    public string AiRecommendation { get; set; } = "";
    public double? AgentConfidence { get; set; }
    public string ActualOutcome { get; set; } = "";
    public double? HumanConfidence { get; set; }
    public string? Notes { get; set; }
    public string ReasonCodesJson { get; set; } = "[]";
    public string UsefulEvidenceIdsJson { get; set; } = "[]";
    public string MisleadingEvidenceIdsJson { get; set; } = "[]";
    public string Source { get; set; } = "";
    public string ValidationStatus { get; set; } = "";
    public string? ValidatedBy { get; set; }
    public DateTimeOffset? ValidatedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
