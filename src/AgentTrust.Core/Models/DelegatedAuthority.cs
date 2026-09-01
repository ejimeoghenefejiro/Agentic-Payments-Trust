namespace AgentTrust.Core.Models;

public sealed record DelegatedAuthority(
    string AuthorityId,
    string AgentId,
    IReadOnlyCollection<string> Permissions,
    decimal PerTransactionLimit,
    decimal DailyLimit,
    IReadOnlyCollection<string> ApprovedMerchants,
    IReadOnlyCollection<string> CategoryScope,
    string GeographicScope,
    TimeOnly? WindowStart,
    TimeOnly? WindowEnd,
    decimal HumanApprovalAbove,
    DateOnly Expiry,
    bool Revoked)
{
    public bool IsActive(DateOnly asOf) => !Revoked && asOf <= Expiry;

    public bool PermitsAction(string action) =>
        Permissions.Contains(action, StringComparer.OrdinalIgnoreCase);

    public bool PermitsMerchant(string merchant) =>
        ApprovedMerchants.Count == 0 ||
        ApprovedMerchants.Contains(merchant, StringComparer.OrdinalIgnoreCase);

    public bool WithinTimeWindow(TimeOnly asOf)
    {
        if (WindowStart is null || WindowEnd is null) return true;
        return asOf >= WindowStart && asOf <= WindowEnd;
    }

    public bool RequiresHumanApproval(decimal amount) => amount > HumanApprovalAbove;
}
