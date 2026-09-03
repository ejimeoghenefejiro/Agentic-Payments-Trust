using Microsoft.AspNetCore.Identity;

namespace AgentTrust.Data;

/// <summary>Local identity linked to exactly one trusted external OIDC subject.</summary>
public sealed class ApplicationUser : IdentityUser
{
    public string PrincipalId { get; set; } = "";
    public string ExternalIssuer { get; set; } = "";
    public string ExternalSubject { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; } = 1;
}
