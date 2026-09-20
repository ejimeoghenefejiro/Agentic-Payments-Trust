using System.Reflection;
using System.Security.Cryptography;
using AgentTrust.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("health/version")]
public sealed class HealthVersionController(
    IWebHostEnvironment environment,
    IServiceProvider services) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<RuntimeVersionResponse>> Get(CancellationToken cancellationToken)
    {
        var assembly = typeof(Program).Assembly;
        var path = assembly.Location;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var gitCommit = informationalVersion?.Split('+', 2).ElementAtOrDefault(1)
            ?? assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "RepositoryCommit")?.Value;
        string? migration = null;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetService<AgentTrustDbContext>();
            if (db is not null)
                migration = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault();
        }
        await using var assemblyStream = System.IO.File.OpenRead(path);
        var binarySha256 = Convert.ToHexString(await SHA256.HashDataAsync(assemblyStream, cancellationToken));

        return Ok(new RuntimeVersionResponse(
            assembly.GetName().Name ?? "AgentTrust.Api",
            informationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown",
            assembly.GetName().Version?.ToString() ?? "unknown",
            gitCommit,
            System.IO.File.GetLastWriteTimeUtc(path),
            environment.EnvironmentName,
            migration,
            binarySha256,
            assembly.ManifestModule.ModuleVersionId.ToString("D")));
    }
}

public sealed record RuntimeVersionResponse(
    string Application,
    string ApplicationVersion,
    string AssemblyVersion,
    string? GitCommitSha,
    DateTime BuildTimestampUtc,
    string Environment,
    string? SchemaMigrationVersion,
    string BinarySha256,
    string ModuleVersionId);
