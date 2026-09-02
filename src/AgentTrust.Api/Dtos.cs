namespace AgentTrust.Api;

public sealed record RegisterAgentRequest(
    string AgentId, string PrincipalId, string AgentType = "procurement", string Environment = "production",
    DateTimeOffset? IssuedAt = null, DateTimeOffset? ExpiresAt = null, string Issuer = "agent-trust-ca");

public sealed record RegisterPrincipalRequest(string PrincipalId, string Name);

public sealed record CreateBindingRequest(string AgentId, string PrincipalId, string BindingEvidenceRef = "");

public sealed record GrantAuthorityRequest(
    string AuthorityId, string AgentId, List<string> Permissions, decimal PerTransactionLimit, decimal DailyLimit,
    List<string> ApprovedMerchants, List<string> CategoryScope, string GeographicScope, decimal HumanApprovalAbove,
    DateOnly Expiry);

public sealed record EvidenceItemDto(string EvidenceId, string Type, string Description, bool Exists = true);

/// <summary>
/// A single request DTO covers both modes: set UserInstruction for agent-driven natural-language
/// execution (Priority 6 requirement); omit it and set Action/Merchant/Amount directly for the
/// deterministic direct-injection path used by the scenario suite.
/// </summary>
/// <summary>
/// CandidateEvent is optional: when set, AgentTrust.Intelligence investigates it first (building
/// on whatever history has been recorded via POST /api/intelligence/events for this customer/
/// merchant) and its evidence is merged into the transaction's EvidenceManifest before the trust
/// layer decides — the doc's Financial Intelligence Layer -> proposed action -> Trust Layer flow,
/// in one call. The investigation's recommendation is advisory only; it cannot authorise or block
/// anything by itself, and the response reports it separately from the trust layer's decision.
/// </summary>
public sealed record TransactionRequest(
    string TransactionId, string AgentId, string PrincipalId,
    string? UserInstruction, string ExpectedCurrency,
    string? Action, string? Merchant, string? Category, decimal? Amount, string? Reason, string? IdempotencyKey,
    List<EvidenceItemDto> Evidence, Dictionary<string, string>? Context,
    string? ScriptedAgentResponse,
    TransactionEventDto? CandidateEvent = null);

public sealed record ApprovalDecisionRequest(bool Approve, string Approver, string? Reason);
