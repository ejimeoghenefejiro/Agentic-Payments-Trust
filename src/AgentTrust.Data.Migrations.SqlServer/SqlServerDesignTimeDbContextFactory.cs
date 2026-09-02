using AgentTrust.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentTrust.Data.Migrations.SqlServer;

/// <summary>
/// Lets `dotnet ef migrations add` build an AgentTrustDbContext without needing a running app
/// host or a real database — the connection string here is never actually connected to for
/// `migrations add` (only `database update` connects, and this project isn't meant to be run
/// that way; the real app supplies its own connection string at runtime via Program.cs).
/// MigrationsAssembly is set explicitly so these migrations are generated into, and only ever
/// read from, this project — never mixed with the Postgres project's migrations of the same
/// AgentTrustDbContext, which would be a different, incompatible SQL dialect.
/// </summary>
public sealed class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentTrustDbContext>
{
    public AgentTrustDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AgentTrustDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=(local);Database=AgentTrust_DesignTimeOnly;Trusted_Connection=True;TrustServerCertificate=True;",
            x => x.MigrationsAssembly(typeof(SqlServerDesignTimeDbContextFactory).Assembly.FullName));
        return new AgentTrustDbContext(optionsBuilder.Options);
    }
}
