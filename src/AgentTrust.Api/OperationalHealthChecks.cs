using AgentTrust.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentTrust.Api;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentTrustDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("The system-of-record database is reachable.")
                : HealthCheckResult.Unhealthy("The system-of-record database is unavailable.");
        }
        catch (Exception ex) { return HealthCheckResult.Unhealthy("The system-of-record database health check failed.", ex); }
    }
}
