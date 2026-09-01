using AgentTrust.Core;
using AgentTrust.Data;
using AgentTrust.Evidence;
using AgentTrust.Orchestration;
using AgentTrust.Payments;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Connection string comes from configuration/environment only — never hard-code a connection
// string or secret in source. SQL Server takes priority over PostgreSQL if both are set.
var sqlServerConnectionString = builder.Configuration.GetConnectionString("SqlServer")
    ?? Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION");
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
var connectionString = sqlServerConnectionString ?? postgresConnectionString;

if (!string.IsNullOrWhiteSpace(sqlServerConnectionString) || !string.IsNullOrWhiteSpace(postgresConnectionString))
{
    builder.Services.AddDbContext<AgentTrustDbContext>(o =>
    {
        if (!string.IsNullOrWhiteSpace(sqlServerConnectionString)) o.UseSqlServer(sqlServerConnectionString);
        else o.UseNpgsql(postgresConnectionString);
    });

    builder.Services.AddScoped<IAgentRegistry, EfAgentRegistry>();
    builder.Services.AddScoped<IPrincipalStore, EfPrincipalStore>();
    builder.Services.AddScoped<IMerchantStore, EfMerchantStore>();
    builder.Services.AddScoped<IPrincipalBindingStore, EfPrincipalBindingStore>();
    builder.Services.AddScoped<IDelegatedAuthorityStore, EfDelegatedAuthorityStore>();
    builder.Services.AddScoped<ITransactionLedger, EfTransactionLedger>();
    builder.Services.AddScoped<ITransactionIntentStore, EfTransactionIntentStore>();
    builder.Services.AddScoped<IEvidenceManifestStore, EfEvidenceManifestStore>();
    builder.Services.AddScoped<IPolicyDecisionStore, EfPolicyDecisionStore>();
    builder.Services.AddScoped<IPaymentOutcomeStore, EfPaymentOutcomeStore>();
    builder.Services.AddScoped<IApprovalStore, EfApprovalStore>();
    builder.Services.AddScoped<IAuditRecordStore, EfAuditRecordStore>();
}
else
{
    // No database configured: fall back to process-wide in-memory stores so the API is still
    // runnable for local exploration/demo without standing up Postgres first.
    builder.Services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
    builder.Services.AddSingleton<IPrincipalStore, InMemoryPrincipalStore>();
    builder.Services.AddSingleton<IMerchantStore, InMemoryMerchantStore>();
    builder.Services.AddSingleton<IPrincipalBindingStore, InMemoryPrincipalBindingStore>();
    builder.Services.AddSingleton<IDelegatedAuthorityStore, InMemoryDelegatedAuthorityStore>();
    builder.Services.AddSingleton<ITransactionLedger, InMemoryTransactionLedger>();
    builder.Services.AddSingleton<ITransactionIntentStore, InMemoryTransactionIntentStore>();
    builder.Services.AddSingleton<IEvidenceManifestStore, InMemoryEvidenceManifestStore>();
    builder.Services.AddSingleton<IPolicyDecisionStore, InMemoryPolicyDecisionStore>();
    builder.Services.AddSingleton<IPaymentOutcomeStore, InMemoryPaymentOutcomeStore>();
    builder.Services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
    builder.Services.AddSingleton<IAuditRecordStore, InMemoryAuditRecordStore>();
}

builder.Services.AddScoped<IPaymentAdapter, MockPaymentAdapter>();

builder.Services.AddScoped<TrustFramework>(sp => new TrustFramework(
    sp.GetRequiredService<IAgentRegistry>(),
    sp.GetRequiredService<IPrincipalBindingStore>(),
    sp.GetRequiredService<IDelegatedAuthorityStore>(),
    sp.GetRequiredService<ITransactionLedger>(),
    sp.GetRequiredService<IPaymentAdapter>(),
    sp.GetRequiredService<ITransactionIntentStore>(),
    sp.GetRequiredService<IEvidenceManifestStore>(),
    sp.GetRequiredService<IPolicyDecisionStore>(),
    sp.GetRequiredService<IPaymentOutcomeStore>(),
    sp.GetRequiredService<IApprovalStore>(),
    sp.GetRequiredService<IAuditRecordStore>()));

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(connectionString))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AgentTrustDbContext>().Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { } // exposed for WebApplicationFactory-based integration tests
