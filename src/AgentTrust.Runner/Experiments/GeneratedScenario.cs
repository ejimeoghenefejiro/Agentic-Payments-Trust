using AgentTrust.Core.Models;

namespace AgentTrust.Runner.Experiments;

/// <summary>
/// A fully self-contained, isolated scenario: its own agent/principal/authority so it never
/// interferes with any other generated scenario when run against a shared audit ledger, plus
/// its ground-truth expected outcome derived from the same rule the policy engine implements.
/// </summary>
public sealed class GeneratedScenario
{
    public required string ScenarioId { get; init; }
    public required ScenarioCategory Category { get; init; }
    public required Decision ExpectedDecision { get; init; }
    public required string? ExpectedReasonCode { get; init; }
    public required PaymentStatus ExpectedPaymentStatus { get; init; }

    public required AgentIdentity Identity { get; init; }
    public required PrincipalBinding Binding { get; init; }
    public required DelegatedAuthority Authority { get; init; }
    public required TransactionIntent Intent { get; init; }
    public required EvidenceManifest EvidenceManifest { get; init; }

    public bool ForcePaymentFailure { get; init; }
    public bool SeedPriorApprovedDuplicate { get; init; }
    public decimal PreExistingDailySpend { get; init; }
}
