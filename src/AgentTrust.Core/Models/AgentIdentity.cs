namespace AgentTrust.Core.Models;

public enum CredentialStatus
{
    Active,
    Suspended,
    Revoked,
    Expired
}

public sealed record AgentIdentity(
    string AgentId,
    string PrincipalId,
    string AgentType,
    string Environment,
    CredentialStatus CredentialStatus,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string IssuerTrustAnchor)
{
    public bool IsValid(DateTimeOffset asOf) =>
        CredentialStatus == CredentialStatus.Active &&
        asOf >= IssuedAt &&
        asOf <= ExpiresAt;
}
