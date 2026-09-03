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
            || principal.HasClaim(x => x.Type == AgentTrustClaimTypes.PrincipalId)) return principal;

        var issuer = principal.FindFirst("iss")?.Value;
        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) return principal;

        var loginProvider = ProviderForIssuer(issuer);
        var user = await _users.FindByLoginAsync(loginProvider, subject);
        if (user is null && _configuration.GetValue("Authentication:AutoProvisionUsers", false))
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid().ToString("N"), PrincipalId = $"principal_{Guid.NewGuid():N}",
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

        identity.AddClaim(new Claim(AgentTrustClaimTypes.PrincipalId, user.PrincipalId));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.PrincipalId));
        return principal;
    }

    private static string ProviderForIssuer(string issuer) =>
        $"oidc:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer)))}";
}
