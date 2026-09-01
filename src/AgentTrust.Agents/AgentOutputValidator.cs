using AgentTrust.Core.Models;

namespace AgentTrust.Agents;

/// <summary>
/// Validates raw, untrusted LLM output before it is allowed to become a TransactionIntent.
/// Invalid or incomplete output must never reach the policy engine or payment adapter.
/// </summary>
public static class AgentOutputValidator
{
    public static (bool IsValid, TransactionIntent? Intent, List<string> ReasonCodes) Validate(
        RawAgentOutput? output, AgentProposalContext context)
    {
        var reasons = new List<string>();

        if (output is null || string.IsNullOrWhiteSpace(output.Action))
        {
            reasons.Add("INVALID_AGENT_OUTPUT");
            return (false, null, reasons);
        }

        if (output.Amount is null || output.Amount <= 0)
        {
            reasons.Add("MISSING_TRANSACTION_AMOUNT");
        }

        if (string.IsNullOrWhiteSpace(output.Merchant))
        {
            reasons.Add("UNKNOWN_MERCHANT");
        }

        if (!string.IsNullOrWhiteSpace(output.Currency) &&
            !string.Equals(output.Currency, context.ExpectedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("CURRENCY_MISMATCH");
        }

        if (output.EvidenceIds is null || output.EvidenceIds.Count == 0)
        {
            reasons.Add("MISSING_EVIDENCE");
        }
        else
        {
            var knownIds = context.AvailableEvidence.Select(e => e.EvidenceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (output.EvidenceIds.Any(id => !knownIds.Contains(id)))
            {
                reasons.Add("INVALID_EVIDENCE_REFERENCE");
            }
        }

        if (reasons.Count > 0)
        {
            return (false, null, reasons);
        }

        var citedEvidence = context.AvailableEvidence
            .Where(e => output.EvidenceIds!.Contains(e.EvidenceId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var action = string.IsNullOrWhiteSpace(output.Category) ? output.Action! : $"{output.Action}:{output.Category}";

        var intent = new TransactionIntent(
            context.TransactionId,
            context.AgentId,
            context.PrincipalId,
            action,
            output.Merchant!,
            output.Category ?? string.Empty,
            output.Amount!.Value,
            output.Reason ?? string.Empty,
            citedEvidence,
            context.OccurredAt,
            IdempotencyKey: context.TransactionId);

        return (true, intent, reasons);
    }
}
