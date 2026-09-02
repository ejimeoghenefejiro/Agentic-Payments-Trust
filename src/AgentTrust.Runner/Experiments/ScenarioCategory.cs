namespace AgentTrust.Runner.Experiments;

/// <summary>
/// Ground-truth category for a generated scenario. Each maps to exactly one distinguishable
/// PolicyEngine code path (see AgentTrust.Policy.PolicyEngine), so "expected decision/reason"
/// is derived from the same logic the policy engine implements, not guessed.
/// </summary>
public enum ScenarioCategory
{
    Legitimate,
    TransactionLimitViolation,
    DailyLimitViolation,
    MerchantViolation,
    ExpiredAuthority,
    RevokedAuthority,
    RevokedAgent,
    EvidenceDeficiency,
    PromptInjection,
    DuplicatePayment,
    AuthorityScopeViolation,
    HumanApprovalRequired,
    ConflictingInstructions,
    PriceAnomaly,
    CredentialAttack,
    ProviderFailure
}

public static class ScenarioCategoryExtensions
{
    /// <summary>Categories representing an adversarial/attack attempt, for the derived
    /// attack-success/prevention/false-positive/false-negative metrics (Priority 4).</summary>
    public static readonly ScenarioCategory[] AdversarialCategories =
    {
        ScenarioCategory.PromptInjection,
        ScenarioCategory.DuplicatePayment,
        ScenarioCategory.CredentialAttack,
        ScenarioCategory.AuthorityScopeViolation
    };

    /// <summary>Categories where a correctly functioning system must escalate, used for the
    /// Human Escalation Accuracy metric.</summary>
    public static readonly ScenarioCategory[] EscalationCategories =
    {
        ScenarioCategory.MerchantViolation,
        ScenarioCategory.EvidenceDeficiency,
        ScenarioCategory.HumanApprovalRequired,
        ScenarioCategory.ConflictingInstructions,
        ScenarioCategory.PriceAnomaly
    };

    /// <summary>Categories where the transaction must ultimately be approved (legitimate
    /// activity, including a provider-side failure that is still a correct trust decision).</summary>
    public static readonly ScenarioCategory[] AuthorizedCategories =
    {
        ScenarioCategory.Legitimate,
        ScenarioCategory.ProviderFailure
    };
}
