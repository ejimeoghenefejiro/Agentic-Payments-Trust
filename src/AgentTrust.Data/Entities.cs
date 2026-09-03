namespace AgentTrust.Data;

// Flat, EF-friendly persistence shapes. Nested collections (permissions, evidence lists,
// policy checks, etc.) are stored as JSON text columns via value converters in
// AgentTrustDbContext, rather than normalised child tables — this keeps the schema simple
// and matches how the domain already treats these as atomic, versioned payloads.

public sealed class AgentEntity
{
    public string AgentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string AgentType { get; set; } = "";
    public string Environment { get; set; } = "";
    public string CredentialStatus { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string IssuerTrustAnchor { get; set; } = "";
}

public sealed class PrincipalEntity
{
    public string PrincipalId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class MerchantEntity
{
    public string MerchantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Approved { get; set; }
}

public sealed class PrincipalBindingEntity
{
    public string AgentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public DateTimeOffset BoundAt { get; set; }
    public bool Active { get; set; }
    public string BindingEvidenceRef { get; set; } = "";
}

public sealed class DelegatedAuthorityEntity
{
    public string AuthorityId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
    public decimal PerTransactionLimit { get; set; }
    public decimal? DailyLimit { get; set; }
    public List<string> ApprovedMerchants { get; set; } = new();
    public List<string> CategoryScope { get; set; } = new();
    public string GeographicScope { get; set; } = "";
    public TimeOnly? WindowStart { get; set; }
    public TimeOnly? WindowEnd { get; set; }
    public decimal HumanApprovalAbove { get; set; }
    public DateOnly Expiry { get; set; }
    public bool Revoked { get; set; }
}

public sealed class TransactionIntentEntity
{
    public string TransactionId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Merchant { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string EvidenceJson { get; set; } = "[]";
    public DateTimeOffset RequestedAt { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class EvidenceManifestEntity
{
    public string TransactionId { get; set; } = "";
    public string CitedEvidenceJson { get; set; } = "[]";
    public string RequiredEvidenceTypesJson { get; set; } = "[]";
}

public sealed class PolicyDecisionEntity
{
    public string TransactionId { get; set; } = "";
    public string Decision { get; set; } = "";
    public string ChecksJson { get; set; } = "[]";
    public string PolicyVersion { get; set; } = "";
    public string ReasonCodesJson { get; set; } = "[]";
}

public sealed class PaymentOutcomeEntity
{
    public string TransactionId { get; set; } = "";
    public string Status { get; set; } = "";
    public string ProviderReference { get; set; } = "";
    public string? FailureReason { get; set; }
}

public sealed class ApprovalRequestEntity
{
    public string ApprovalId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string OriginalDecision { get; set; } = "";
    public string? Approver { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Reason { get; set; }
    public string? FinalOutcome { get; set; }
}

public sealed class AuditRecordEntity
{
    public int SequenceNumber { get; set; }
    public string TransactionId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string AuthorityId { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public string RecordJson { get; set; } = "";
    public string PreviousHash { get; set; } = "";
    public string CurrentHash { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
}
