using Microsoft.AspNetCore.Authorization;

namespace AgentTrust.Api.Authentication;

public sealed class StablePrincipalRequirement : IAuthorizationRequirement;
public sealed class StablePrincipalHandler : AuthorizationHandler<StablePrincipalRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StablePrincipalRequirement requirement)
    {
        if (context.User.HasClaim(x => x.Type == AgentTrustClaimTypes.PrincipalId)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class StepUpRequirement : IAuthorizationRequirement;
public sealed class StepUpHandler : AuthorizationHandler<StepUpRequirement>
{
    private readonly IConfiguration _configuration;
    public StepUpHandler(IConfiguration configuration) => _configuration = configuration;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StepUpRequirement requirement)
    {
        var amr = context.User.FindAll("amr").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var acr = context.User.FindFirst("acr")?.Value;
        var allowedAcr = _configuration.GetSection("Authentication:StepUp:AllowedAcrValues").Get<string[]>() ?? [];
        var methodSatisfied = amr.Contains("mfa", StringComparer.OrdinalIgnoreCase)
            || (acr is not null && allowedAcr.Contains(acr, StringComparer.Ordinal));
        var authTimeText = context.User.FindFirst("auth_time")?.Value;
        var maxAge = TimeSpan.FromMinutes(_configuration.GetValue("Authentication:StepUp:MaxAgeMinutes", 10));
        var now = DateTimeOffset.UtcNow;
        var recent = long.TryParse(authTimeText, out var authTimeSeconds)
            && DateTimeOffset.FromUnixTimeSeconds(authTimeSeconds) <= now
            && now - DateTimeOffset.FromUnixTimeSeconds(authTimeSeconds) <= maxAge;
        if (methodSatisfied && recent) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
