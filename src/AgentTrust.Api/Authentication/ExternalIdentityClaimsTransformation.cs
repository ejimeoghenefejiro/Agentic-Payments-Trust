using System.Security.Claims;
using AgentTrust.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;
using System.Text;

namespace AgentTrust.Api.Authentication;

public static class AgentTrustClaimTypes
{
    public const string PrincipalId = "agenttrust_principal_id";
    public const string IdentityLinked = "agenttrust_identity_linked";
}

/// <summary>Links a validated OIDC issuer/subject pair to a stable local Identity user.</summary>
public sealed class ExternalIdentityClaimsTransformation : IClaimsTransformation
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IConfiguration _configuration;
    public ExternalIdentityClaimsTransformation(UserManager<ApplicationUser> users, IConfiguration configuration)
    { _users = users; _configuration = configuration; }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated
            || principal.HasClaim(x => x.Type == AgentTrustClaimTypes.IdentityLinked)) return principal;

        var issuer = principal.FindFirst("iss")?.Value;
        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) return principal;

        var loginProvider = LoginProviderForIssuer(issuer);
        var user = await _users.FindByLoginAsync(loginProvider, subject);
        if (user is null && _configuration.GetValue("Authentication:AutoProvisionUsers", false))
        {
            // Only the local development issuer may propose its deterministic principal id.
            // Production OIDC principal ids always originate in this Identity store, never in
            // an externally supplied token claim.
            var developmentPrincipalId = string.Equals(issuer, "urn:agenttrust:development", StringComparison.Ordinal)
                ? principal.FindFirst(AgentTrustClaimTypes.PrincipalId)?.Value
                : null;
            user = new ApplicationUser
            {
                Id = Guid.NewGuid().ToString("N"),
                PrincipalId = string.IsNullOrWhiteSpace(developmentPrincipalId)
                    ? $"principal_{Guid.NewGuid():N}"
                    : developmentPrincipalId,
                UserName = $"oidc_{Guid.NewGuid():N}", ExternalIssuer = issuer,
                ExternalSubject = subject, CreatedAt = DateTimeOffset.UtcNow
            };
            var created = await _users.CreateAsync(user);
            if (!created.Succeeded)
                throw new InvalidOperationException($"Identity provisioning failed: {string.Join(',', created.Errors.Select(x => x.Code))}");
            var linked = await _users.AddLoginAsync(user, new UserLoginInfo(loginProvider, subject, issuer));
            if (!linked.Succeeded)
                throw new InvalidOperationException($"Identity link failed: {string.Join(',', linked.Errors.Select(x => x.Code))}");
        }
        if (user is null) return principal;

        // Replace token-provided identity mappings with the durable local mapping. This prevents
        // an issuer claim from selecting another user's PrincipalId and keeps repeat requests tied
        // to the same issuer/subject record.
        foreach (var claim in identity.FindAll(AgentTrustClaimTypes.PrincipalId).ToArray())
            identity.RemoveClaim(claim);
        foreach (var claim in identity.FindAll(ClaimTypes.NameIdentifier).ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(AgentTrustClaimTypes.PrincipalId, user.PrincipalId));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.PrincipalId));
        identity.AddClaim(new Claim(AgentTrustClaimTypes.IdentityLinked, "true"));
        return principal;
    }

    public static string LoginProviderForIssuer(string issuer) =>
        $"oidc:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer)))}";
}
