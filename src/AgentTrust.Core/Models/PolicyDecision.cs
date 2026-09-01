namespace AgentTrust.Core.Models;

public enum Decision
{
    Approve,
    Deny,
    Escalate
}

public sealed record PolicyCheck(string Name, bool Passed, string Detail);

public sealed record PolicyDecisionResult(
    string TransactionId,
    Decision Decision,
    IReadOnlyList<PolicyCheck> Checks,
    string PolicyVersion,
    IReadOnlyList<string> ReasonCodes);
