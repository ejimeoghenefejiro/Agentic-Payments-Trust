namespace AgentTrust.Core.Models;

public sealed record EvidenceManifest(
    string TransactionId,
    IReadOnlyList<EvidenceItem> CitedEvidence,
    IReadOnlyList<string> RequiredEvidenceTypes)
{
    public IReadOnlyList<EvidenceItem> ValidCitedEvidence =>
        CitedEvidence.Where(e => e.Exists).ToList();

    public IReadOnlyList<EvidenceItem> InvalidCitedEvidence =>
        CitedEvidence.Where(e => !e.Exists).ToList();

    /// <summary>Precision: fraction of cited evidence that is valid/relevant.</summary>
    public double Precision => CitedEvidence.Count == 0
        ? 0
        : (double)ValidCitedEvidence.Count / CitedEvidence.Count;

    /// <summary>Recall: fraction of required evidence types actually covered by valid citations.</summary>
    public double Recall
    {
        get
        {
            if (RequiredEvidenceTypes.Count == 0) return 1.0;
            var coveredTypes = ValidCitedEvidence.Select(e => e.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var covered = RequiredEvidenceTypes.Count(t => coveredTypes.Contains(t));
            return (double)covered / RequiredEvidenceTypes.Count;
        }
    }

    public double F1 => (Precision + Recall) == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public sealed record AuditRecord(
    string TransactionId,
    string AgentId,
    string PrincipalId,
    string AuthorityId,
    EvidenceManifest Evidence,
    string PolicyVersion,
    PolicyDecisionResult PolicyDecision,
    PaymentResult PaymentResult,
    DateTimeOffset Timestamp,
    string EvidenceHash);
