using AgentTrust.Core.Models;
using AgentTrust.Mandates;

namespace AgentTrust.Tasks;

public enum TaskExecutionDecision
{
    Approve,
    Deny,
    Escalate
}

public sealed record TaskExecutionResult(
    string TaskExecutionId,
    string TaskId,
    string MandateId,
    decimal ProposedAmount,
    TaskExecutionDecision Decision,
    IReadOnlyList<string> Reasons,
    MandateCheckResult MandateCheck,
    Decision? TrustLayerDecision,
    PaymentStatus PaymentStatus,
    bool AwaitingHumanApproval);
