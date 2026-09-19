namespace AgentTrust.Mandates;

public enum MandateStatus
{
    Active,
    Suspended,
    Expired,
    Superseded
}

public enum AboveLimitAction
{
    RequireApproval,
    Block
}

/// <summary>
/// Answers "how may money be used for this particular task" — narrower than, and layered on top
/// of, the trust layer's DelegatedAuthority ("what is the agent allowed to do at all"). Matches
/// a bounded recurring task with a per-action cap, schedule and require-approval-above-limit policy.
/// TaskParameters carries task-specific matching fields generically, so this type is not coupled
/// to any one business domain.
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
    public int Version { get; init; } = 1;
    public string? SupersedesMandateId { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; } = CreatedAt;
    public decimal? DailyLimit { get; init; }

    public bool IsActive(DateTimeOffset asOf) => Status == MandateStatus.Active && asOf <= ExpiresAt;
}
