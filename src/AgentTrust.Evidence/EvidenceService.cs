using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentTrust.Core.Models;

namespace AgentTrust.Evidence;

public sealed class EvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string ComputeEvidenceHash(EvidenceManifest manifest)
    {
        var canonical = JsonSerializer.Serialize(manifest, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public AuditRecord BuildAuditRecord(
        TransactionIntent intent,
        string authorityId,
        EvidenceManifest evidence,
        PolicyDecisionResult policyDecision,
        PaymentResult paymentResult,
        DateTimeOffset timestamp) =>
        new(
            intent.TransactionId,
            intent.AgentId,
            intent.PrincipalId,
            authorityId,
            evidence,
            policyDecision.PolicyVersion,
            policyDecision,
            paymentResult,
            timestamp,
            ComputeEvidenceHash(evidence));
}
