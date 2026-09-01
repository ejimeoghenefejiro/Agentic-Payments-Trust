using AgentTrust.Core.Models;

namespace AgentTrust.Agents;

/// <summary>
/// Reference autonomous financial agent. The agent proposes a transaction intent from a
/// natural-language instruction and contextual evidence; it never decides authorisation
/// itself — that is the trust framework's job (see AgentTrust.Policy.PolicyEngine and
/// AgentOutputValidator). This separation is the framework's central design principle:
/// probabilistic agent reasoning vs. deterministic policy enforcement.
/// </summary>
public interface IPaymentAgent
{
    string AgentId { get; }
    Task<AgentProposalResult> ProposeTransactionAsync(AgentProposalContext context, CancellationToken cancellationToken = default);
}

public sealed record AgentProposalContext(
    string TransactionId,
    string AgentId,
    string PrincipalId,
    string UserInstruction,
    IReadOnlyList<EvidenceItem> AvailableEvidence,
    IReadOnlyDictionary<string, string> Context,
    string ExpectedCurrency,
    DateTimeOffset OccurredAt);

/// <summary>Raw, untrusted structured output as returned by the LLM before validation.</summary>
public sealed record RawAgentOutput(
    string? Action,
    string? Category,
    string? Merchant,
    decimal? Amount,
    string? Currency,
    string? Reason,
    IReadOnlyList<string>? EvidenceIds);

public enum AgentOutputStatus
{
    Valid,
    Invalid
}

public sealed record AgentProposalResult(
    AgentOutputStatus Status,
    TransactionIntent? Intent,
    RawAgentOutput? RawOutput,
    string? RawResponseText,
    IReadOnlyList<string> ValidationReasonCodes,
    long AgentLatencyMs);
