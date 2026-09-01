namespace AgentTrust.Core.Models;

public sealed record PrincipalBinding(
    string AgentId,
    string PrincipalId,
    DateTimeOffset BoundAt,
    bool Active,
    string BindingEvidenceRef)
{
    public bool IsValidFor(string agentId, string principalId) =>
        Active && AgentId == agentId && PrincipalId == principalId;
}
