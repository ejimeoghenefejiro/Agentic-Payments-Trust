using AgentTrust.Core.Models;

namespace AgentTrust.Mandates;

/// <summary>
/// Converts a FinancialMandate into the frozen core's DelegatedAuthority so the existing,
/// unmodified PolicyEngine performs the actual amount/merchant/scope authorisation — the
/// Mandate layer never re-implements what the trust layer already does deterministically.
/// humanApprovalAboveOverride lets a human's explicit one-off approval (see
/// TaskExecutionOrchestrator.ResolveEscalation) clear a specific amount through without
/// permanently raising the mandate's own limit — the mandate itself is never mutated by this.
/// </summary>
public static class MandateToAuthorityMapper
{
    public static DelegatedAuthority ToAuthority(FinancialMandate mandate, decimal? oneOffApprovedAmount = null)
    {
        // oneOffApprovedAmount, when set, scopes an elevated limit to a single human-approved
        // transaction (see TaskExecutionOrchestrator.ResolveEscalation) — the caller must grant
        // this, make exactly one ProcessTransaction call, then immediately re-grant the normal
        // mandate authority, so the elevation never persists as a standing increase. The mandate
        // itself is never mutated: only the principal re-authorising via a new mandate version
        // can permanently raise a limit (doc rule: the agent may never increase its own limit).
        var effectiveLimit = oneOffApprovedAmount ?? mandate.PerTransactionLimit;
        return new DelegatedAuthority(
            AuthorityId: $"authority_for_{mandate.MandateId}",
            AgentId: mandate.AgentId,
            Permissions: new[] { $"purchase:{mandate.Purpose}" },
            PerTransactionLimit: effectiveLimit,
            // The atomic mandate reservation is authoritative for daily/weekly/monthly usage.
            // Mirror an explicit daily cap here, but do not accidentally turn the per-transaction
            // limit into a one-transaction-per-day limit when no daily cap was granted.
            DailyLimit: oneOffApprovedAmount is not null ? decimal.MaxValue : mandate.DailyLimit ?? decimal.MaxValue,
            ApprovedMerchants: new[] { mandate.Merchant },
            CategoryScope: new[] { mandate.Purpose },
            GeographicScope: "ANY",
            WindowStart: null,
            WindowEnd: null,
            HumanApprovalAbove: effectiveLimit,
            Expiry: DateOnly.FromDateTime(mandate.ExpiresAt.UtcDateTime),
            Revoked: mandate.Status != MandateStatus.Active);
    }
}
