using AgentTrust.Core.Models;

namespace AgentTrust.Runner;

public sealed class ScenarioDefinition
{
    public string ScenarioId { get; set; } = "";
    public string Description { get; set; } = "";
    public string ExpectedDecision { get; set; } = ""; // Approve | Deny | Escalate

    public AgentIdentityDto Identity { get; set; } = new();
    public PrincipalBindingDto? Binding { get; set; } = new();
    public DelegatedAuthorityDto? Authority { get; set; } = new();
    public TransactionIntentDto Intent { get; set; } = new();
    public List<EvidenceItemDto> Evidence { get; set; } = new();
    public List<string> RequiredEvidenceTypes { get; set; } = new();
    public bool ForcePaymentFailure { get; set; }
    public decimal PreExistingDailySpend { get; set; }
    public bool SimulatePriorApprovedDuplicate { get; set; }

    // Agent mode (Priority 2): when UserInstruction is set, the runner invokes a real
    // IPaymentAgent (Semantic Kernel) instead of injecting Intent directly. ScriptedAgentResponse
    // supplies the deterministic "model output" (raw JSON text) so the scenario stays
    // reproducible without a live API key. Leave both unset for direct-injection (policy-only) mode.
    public string? UserInstruction { get; set; }
    public Dictionary<string, string> Context { get; set; } = new();
    public string ExpectedCurrency { get; set; } = "NGN";
    public string? ScriptedAgentResponse { get; set; }

    // Priority 8 (cross-model experiments): ground truth for whether a well-behaved agent
    // should produce schema-valid output for this scenario, independent of what the policy
    // engine then decides. Null for direct-injection scenarios (not applicable).
    public bool? ExpectedAgentOutputValid { get; set; }
}

public sealed class AgentIdentityDto
{
    public string AgentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string AgentType { get; set; } = "procurement";
    public string Environment { get; set; } = "production";
    public string CredentialStatus { get; set; } = "Active"; // Active|Suspended|Revoked|Expired
    public string IssuedAt { get; set; } = "2027-01-01T00:00:00Z";
    public string ExpiresAt { get; set; } = "2027-12-31T00:00:00Z";
    public string Issuer { get; set; } = "agent-trust-ca";
}

public sealed class PrincipalBindingDto
{
    public bool Active { get; set; } = true;
    public string BindingEvidenceRef { get; set; } = "binding_doc_001";
}

public sealed class DelegatedAuthorityDto
{
    public string AuthorityId { get; set; } = "auth_001";
    public List<string> Permissions { get; set; } = new() { "purchase:fuel" };
    public decimal PerTransactionLimit { get; set; } = 50000;
    public decimal DailyLimit { get; set; } = 200000;
    public List<string> ApprovedMerchants { get; set; } = new();
    public List<string> CategoryScope { get; set; } = new();
    public string GeographicScope { get; set; } = "NG";
    public string? WindowStart { get; set; }
    public string? WindowEnd { get; set; }
    public decimal HumanApprovalAbove { get; set; } = 40000;
    public string Expiry { get; set; } = "2027-12-31";
    public bool Revoked { get; set; }
    public bool Missing { get; set; }
}

public sealed class TransactionIntentDto
{
    public string TransactionId { get; set; } = "";
    public string Action { get; set; } = "purchase:fuel";
    public string Merchant { get; set; } = "";
    public string Category { get; set; } = "fuel";
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string RequestedAt { get; set; } = "2027-06-01T10:00:00Z";
    public string? IdempotencyKey { get; set; }
}

public sealed class EvidenceItemDto
{
    public string EvidenceId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Exists { get; set; } = true;
}
