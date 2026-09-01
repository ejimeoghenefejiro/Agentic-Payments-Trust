namespace AgentTrust.Core.Models;

public sealed record EvidenceItem(string EvidenceId, string Type, string Description, bool Exists);

public sealed record TransactionIntent(
    string TransactionId,
    string AgentId,
    string PrincipalId,
    string Action,
    string Merchant,
    string Category,
    decimal Amount,
    string Reason,
    IReadOnlyCollection<EvidenceItem> Evidence,
    DateTimeOffset RequestedAt,
    string? IdempotencyKey = null);
