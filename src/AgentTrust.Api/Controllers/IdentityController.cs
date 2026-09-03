using AgentTrust.Api.Authentication;
using AgentTrust.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Api.Controllers;

/// <summary>Returns the durable local Identity mapping for the authenticated OIDC subject.</summary>
[ApiController]
[Route("api/identity")]
[Authorize(Policy = "Consumer")]
public sealed class IdentityController(AgentTrustDbContext db) : ControllerBase
{
    /// <summary>
    /// Confirms that the validated JWT issuer/subject has been linked to an ASP.NET Core
    /// Identity user. Local ownership comes exclusively from that durable mapping.
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType<CurrentIdentityResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentIdentityResponse>> Me(CancellationToken cancellationToken)
    {
        var principalId = User.FindFirst(AgentTrustClaimTypes.PrincipalId)?.Value;
        if (string.IsNullOrWhiteSpace(principalId)) return Unauthorized();

        var user = await db.ApplicationUsers.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.PrincipalId == principalId, cancellationToken);
        if (user is null) return Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Local identity mapping was not found.",
            detail: "The authenticated issuer/subject has not been provisioned in the Identity store.");

        return Ok(new CurrentIdentityResponse(
            user.Id,
            user.PrincipalId,
            User.FindFirst("name")?.Value,
            user.ExternalIssuer,
            user.CreatedAt));
    }
}

public sealed record CurrentIdentityResponse(
    string UserId,
    string PrincipalId,
    string? DisplayName,
    string Issuer,
    DateTimeOffset CreatedAt);
