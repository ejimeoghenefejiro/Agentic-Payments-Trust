using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AgentTrust.Api.Authentication;
using AgentTrust.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace AgentTrust.Api.Controllers;

[ApiController,Route("api/development/token"),AllowAnonymous]
public sealed class DevelopmentTokenController(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    UserManager<ApplicationUser> users):ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Issue(DevelopmentTokenRequest request)
    {
        if(!environment.IsDevelopment()||!configuration.GetValue("Authentication:Development:Enabled",false))return NotFound();
        if(string.IsNullOrWhiteSpace(request.Subject)||string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Subject and password are required.");
        var key=configuration["Authentication:Development:SigningKey"]??throw new InvalidOperationException("Development signing key missing.");
        var subject=request.Subject.Trim().ToLowerInvariant();
        var loginProvider=ExternalIdentityClaimsTransformation.LoginProviderForIssuer("urn:agenttrust:development");
        var user=await users.FindByLoginAsync(loginProvider,subject);
        if(user is null||!await users.CheckPasswordAsync(user,request.Password))return Unauthorized();
        var principalId=user.PrincipalId;
        var now=DateTimeOffset.UtcNow;
        var claims=new[]{new Claim("sub",subject),new Claim("name",request.DisplayName??user.UserName??subject),
            new Claim(AgentTrustClaimTypes.PrincipalId,principalId),new Claim(ClaimTypes.NameIdentifier,principalId),new Claim("amr","mfa"),
            new Claim("auth_time",now.ToUnixTimeSeconds().ToString())};
        var token=new JwtSecurityToken("urn:agenttrust:development","agenttrust-development",claims,now.UtcDateTime,now.AddHours(1).UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),SecurityAlgorithms.HmacSha256));
        return Ok(new{accessToken=new JwtSecurityTokenHandler().WriteToken(token),tokenType="Bearer",expiresAt=now.AddHours(1),principalId});
    }
}
public sealed record DevelopmentTokenRequest(string Subject,string Password,string? DisplayName=null);

[ApiController, Route("api/development/users"), AllowAnonymous]
public sealed class DevelopmentUsersController(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    UserManager<ApplicationUser> users,
    AgentTrustDbContext db) : ControllerBase
{
    private const string DevelopmentIssuer = "urn:agenttrust:development";

    /// <summary>
    /// Creates a password-protected local Identity user for development testing. The password is
    /// hashed by ASP.NET Core Identity and is never stored or returned as plaintext. Production
    /// user registration remains the responsibility of the configured OIDC identity provider.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<DevelopmentUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DevelopmentUserResponse>> Create(
        DevelopmentUserRequest request,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()
            || !configuration.GetValue("Authentication:Development:Enabled", false))
            return NotFound();

        var subject = request.Subject?.Trim().ToLowerInvariant();
        var userName = request.UserName?.Trim();
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(userName)
            || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Subject, userName and password are required.");

        var loginProvider = ExternalIdentityClaimsTransformation.LoginProviderForIssuer(DevelopmentIssuer);
        if (await users.FindByLoginAsync(loginProvider, subject) is not null)
            return Conflict("A development user already exists for this subject.");

        var principalId = "dev_principal_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant()[..24];
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString("N"),
            PrincipalId = principalId,
            UserName = userName,
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            NormalizedEmail = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToUpperInvariant(),
            ExternalIssuer = DevelopmentIssuer,
            ExternalSubject = subject,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { errors = created.Errors.Select(error => new { error.Code, error.Description }) });
        }

        var linked = await users.AddLoginAsync(user, new UserLoginInfo(loginProvider, subject, DevelopmentIssuer));
        if (!linked.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { errors = linked.Errors.Select(error => new { error.Code, error.Description }) });
        }

        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            new DevelopmentUserResponse(user.Id, user.PrincipalId, user.UserName!, user.Email, user.ExternalSubject, user.CreatedAt));
    }
}

public sealed record DevelopmentUserRequest(string Subject, string UserName, string Password, string? Email = null);
public sealed record DevelopmentUserResponse(
    string UserId,
    string PrincipalId,
    string UserName,
    string? Email,
    string Subject,
    DateTimeOffset CreatedAt);
