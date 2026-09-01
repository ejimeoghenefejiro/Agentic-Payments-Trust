namespace AgentTrust.Core.Models;

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>
/// Created whenever the policy engine returns ESCALATE. The payment adapter must not execute
/// until a human resolves this to Approved. Records who decided, when, and why, alongside the
/// original policy decision so the full escalation-to-outcome path is auditable.
/// </summary>
public sealed record ApprovalRequest(
    string ApprovalId,
    string TransactionId,
    ApprovalStatus Status,
    DateTimeOffset CreatedAt,
    Decision OriginalDecision,
    string? Approver,
    DateTimeOffset? DecidedAt,
    string? Reason,
    Decision? FinalOutcome);
