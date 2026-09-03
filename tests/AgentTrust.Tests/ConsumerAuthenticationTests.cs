using System.Security.Claims;
using AgentTrust.Api.Authentication;
using AgentTrust.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentTrust.Tests;

public sealed class ConsumerAuthenticationTests
{
    [Fact]
    public async Task StepUpRequiresRecentTrustedMfaEvidence()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Authentication:StepUp:MaxAgeMinutes"] = "10" }).Build();
        var requirement = new StepUpRequirement();
        var recent = new ClaimsPrincipal(new ClaimsIdentity([
            new(AgentTrustClaimTypes.PrincipalId, "principal-1"), new("amr", "mfa"),
            new("auth_time", DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds().ToString())], "test"));
        var context = new AuthorizationHandlerContext([requirement], recent, null);

        await new StepUpHandler(configuration).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task StepUpRejectsStaleMfaAndClientBooleans()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Authentication:StepUp:MaxAgeMinutes"] = "10" }).Build();
        var requirement = new StepUpRequirement();
        var stale = new ClaimsPrincipal(new ClaimsIdentity([
            new(AgentTrustClaimTypes.PrincipalId, "principal-1"), new("amr", "mfa"), new("IsMfa", "true"),
            new("auth_time", DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString())], "test"));
        var context = new AuthorizationHandlerContext([requirement], stale, null);

        await new StepUpHandler(configuration).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task IdentitySchemaEnforcesUniqueIssuerSubjectAndDurableLoginLink()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(connection).Options;
        await using var db = new AgentTrustDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new ApplicationUser { Id = "user-1", PrincipalId = "principal-1", UserName = "oidc-user",
            NormalizedUserName = "OIDC-USER", ExternalIssuer = "https://issuer.example", ExternalSubject = "subject-1", CreatedAt = DateTimeOffset.UtcNow };
        db.ApplicationUsers.Add(user);
        db.ApplicationUserLogins.Add(new IdentityUserLogin<string>
        { UserId = user.Id, LoginProvider = user.ExternalIssuer, ProviderKey = user.ExternalSubject, ProviderDisplayName = "Test OIDC" });
        await db.SaveChangesAsync();

        Assert.Equal("user-1", (await db.ApplicationUserLogins.SingleAsync()).UserId);
        db.ApplicationUsers.Add(new ApplicationUser { Id = "user-2", PrincipalId = "principal-2", UserName = "other",
            NormalizedUserName = "OTHER", ExternalIssuer = user.ExternalIssuer, ExternalSubject = user.ExternalSubject, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
