using AgentTrust.Core.Models;

namespace AgentTrust.Runner.Experiments;

public sealed record ExperimentResult(
    string ScenarioId,
    ScenarioCategory Category,
    Decision ExpectedDecision,
    Decision ActualDecision,
    bool DecisionCorrect,
    string? ExpectedReasonCode,
    IReadOnlyList<string> ActualReasonCodes,
    bool ReasonCodeCorrect,
    PaymentStatus ExpectedPaymentStatus,
    PaymentStatus ActualPaymentStatus,
    bool PaymentStatusCorrect,
    double EvidencePrecision,
    double EvidenceRecall,
    double EvidenceF1,
    long PolicyLatencyMs,
    double WallLatencyMs,
    bool AuditReconstructable);
