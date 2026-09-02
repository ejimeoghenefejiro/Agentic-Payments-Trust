using AgentTrust.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentTrust.Data.Migrations.Postgres;

/// <summary>
/// Lets `dotnet ef migrations add` build an AgentTrustDbContext without needing a running app
/// host or a real database. See SqlServerDesignTimeDbContextFactory in the sibling project for
/// why this is a separate project rather than one set of migrations shared across providers:
/// migrations bake in provider-specific SQL (column types, etc.) at generation time, so a SQL
/// Server migration cannot be replayed against Postgres and vice versa.
/// </summary>
public sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentTrustDbContext>
{
    public AgentTrustDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AgentTrustDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=agenttrust_designtimeonly;Username=postgres;Password=designtimeonly",
            x => x.MigrationsAssembly(typeof(PostgresDesignTimeDbContextFactory).Assembly.FullName));
        return new AgentTrustDbContext(optionsBuilder.Options);
    }
}
