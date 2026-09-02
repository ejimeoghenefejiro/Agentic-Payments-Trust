namespace AgentTrust.Mandates;

public enum MandateStatus
{
    Active,
    Suspended,
    Expired
}

public enum AboveLimitAction
{
    RequireApproval,
    Block
}

/// <summary>
/// Answers "how may money be used for this particular task" — narrower than, and layered on top
/// of, the trust layer's DelegatedAuthority ("what is the agent allowed to do at all"). Matches
/// the doc's worked example: a recurring Uber mandate with a per-trip cap, a fixed route and
/// recipient, a schedule, and a require-approval-above-limit policy.
/// TaskParameters carries task-specific matching fields (pickup/destination/recipient for a
/// ride-booking mandate) generically, so this type isn't coupled to any one task shape.
/// </summary>
public sealed record FinancialMandate(
    string MandateId,
    string PrincipalId,
    string AgentId,
    string Merchant,
    string Purpose,
    string PaymentMethodId,
    decimal PerTransactionLimit,
    decimal? WeeklyLimit,
    decimal? MonthlyLimit,
    string Currency,
    IReadOnlyDictionary<string, string> TaskParameters,
    AboveLimitAction AboveLimit,
    MandateStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsActive(DateTimeOffset asOf) => Status == MandateStatus.Active && asOf <= ExpiresAt;
}
