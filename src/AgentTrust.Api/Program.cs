using AgentTrust.Agents;
using AgentTrust.Core;
using AgentTrust.Data;
using AgentTrust.Evidence;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Learning;
using AgentTrust.Intelligence.Risk;
using AgentTrust.Orchestration;
using AgentTrust.Payments;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.Mandates;
using AgentTrust.PaymentMethods;
using AgentTrust.Scheduling;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

// OpenAI key/model: secret configuration ("OpenAI:ApiKey"/"OpenAI:Model") takes priority over
// OPENAI_API_KEY/OPENAI_MODEL. Credentials must not be stored in appsettings files.
// environment variables. Never put a real key in appsettings.json — only in
// appsettings.Development.json, which is gitignored.
AgentFactory.ConfiguredApiKey = builder.Configuration["OpenAI:ApiKey"];
AgentFactory.ConfiguredModel = builder.Configuration["OpenAI:Model"];

// Connection string comes from configuration/environment only — never hard-code a connection
// string or secret in source. SQL Server takes priority over PostgreSQL if both are set.
var sqlServerConnectionString = builder.Configuration.GetConnectionString("SqlServer")
    ?? Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION");
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
var connectionString = sqlServerConnectionString ?? postgresConnectionString;

if (!string.IsNullOrWhiteSpace(sqlServerConnectionString) || !string.IsNullOrWhiteSpace(postgresConnectionString))
{
    // MigrationsAssembly points at the provider-specific migrations project (see
    // AgentTrust.Data.Migrations.SqlServer / .Postgres) — migrations bake in provider-specific
    // SQL at generation time, so SQL Server's and Postgres's migrations for the same
    // AgentTrustDbContext must never be mixed into one migration set.
    builder.Services.AddDbContext<AgentTrustDbContext>(o =>
    {
        if (!string.IsNullOrWhiteSpace(sqlServerConnectionString))
        {
            o.UseSqlServer(sqlServerConnectionString, x => x.MigrationsAssembly("AgentTrust.Data.Migrations.SqlServer"));
        }
        else
        {
            o.UseNpgsql(postgresConnectionString, x => x.MigrationsAssembly("AgentTrust.Data.Migrations.Postgres"));
        }
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
    builder.Services.AddScoped<ITransactionEventStore, EfTransactionEventStore>();
    builder.Services.AddScoped<IProfileHistoryStore, EfProfileHistoryStore>();
    builder.Services.AddScoped<IInvestigationStateStore, EfInvestigationStateStore>();
    builder.Services.AddScoped<IOutcomeStore, EfOutcomeStore>();
    builder.Services.AddScoped<ISemanticCaseStore, EfSemanticCaseStore>();
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
    builder.Services.AddSingleton<ITransactionEventStore, InMemoryTransactionEventStore>();
    builder.Services.AddSingleton<IProfileHistoryStore, InMemoryProfileHistoryStore>();
    builder.Services.AddSingleton<IInvestigationStateStore, InMemoryInvestigationStateStore>();
    builder.Services.AddSingleton<IOutcomeStore, InMemoryOutcomeStore>();
    builder.Services.AddSingleton<ISemanticCaseStore, InMemorySemanticCaseStore>();
}

builder.Services.AddScoped<IPaymentAdapter, MockPaymentAdapter>();
builder.Services.AddSingleton<IPaymentAttemptStore, InMemoryPaymentAttemptStore>();

// Consumer commerce pilot. Stores remain in-memory until the dedicated consumer schema migration
// is enabled; no raw payment credentials or retailer passwords are accepted by these services.
builder.Services.AddSingleton<IConsumerTaskStore, InMemoryConsumerTaskStore>();
builder.Services.AddSingleton<IConnectedServiceStore, InMemoryConnectedServiceStore>();
builder.Services.AddSingleton<IPurchaseExecutionStore, InMemoryPurchaseExecutionStore>();
builder.Services.AddSingleton<IMandateStore, InMemoryMandateStore>();
builder.Services.AddSingleton<IMandateUsageTracker, InMemoryMandateUsageTracker>();
builder.Services.AddSingleton<IOneOffAuthorisationStore, InMemoryOneOffAuthorisationStore>();
builder.Services.AddSingleton<IScheduledOccurrenceStore, InMemoryScheduledOccurrenceStore>();
builder.Services.AddSingleton<IPaymentMethodStore, InMemoryPaymentMethodStore>();
builder.Services.AddSingleton<IPurchaseAuditSink, InMemoryPurchaseAuditSink>();
builder.Services.AddSingleton(sp => new LivePurchaseGate(new LivePurchaseOptions(
    builder.Configuration.GetValue("LivePurchase:Enabled", false),
    builder.Configuration.GetValue("LivePurchase:MaxPilotAmountGbp", 5m),
    builder.Configuration.GetSection("LivePurchase:AllowedPrincipalIds").Get<string[]>()?.ToHashSet() ?? [],
    builder.Configuration.GetSection("LivePurchase:AllowedMerchantIds").Get<string[]>()?.ToHashSet() ?? [],
    builder.Configuration.GetValue("LivePurchase:RequireExplicitLiveConfirmation", true))));
builder.Services.AddSingleton<IPurchaseAuthorisationService>(_ =>
{
    var encoded = Environment.GetEnvironmentVariable("PURCHASE_AUTHORISATION_KEY");
    if (!string.IsNullOrWhiteSpace(encoded)) return new HmacPurchaseAuthorisationService(Convert.FromBase64String(encoded));
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("PURCHASE_AUTHORISATION_KEY is required outside Development.");
    return new HmacPurchaseAuthorisationService(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
});
builder.Services.AddSingleton<IPlatformPaymentProcessor>(sp =>
{
    var provider = builder.Configuration["Payments:Provider"] ?? "Mock";
    if (!provider.Equals("Stripe", StringComparison.OrdinalIgnoreCase)) return new MockPlatformPaymentProcessor();
    var mode = Enum.Parse<StripePaymentMode>(builder.Configuration["Payments:Mode"] ?? "Test", true);
    return new StripePaymentAdapter(builder.Configuration["Stripe:SecretKey"]
        ?? Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? "",
        new StripePaymentOptions(mode), sp.GetRequiredService<IPaymentMethodStore>());
});
builder.Services.AddSingleton<DemoGroceryConnector>();

// Financial Intelligence layer (AgentTrust.Intelligence). ITransactionEventStore and
// IProfileHistoryStore are registered above (EF-backed and scoped to the request's DbContext
// when a database is configured; singleton in-memory otherwise). Feedback follows the same rule.
//
// InvestigationAgent/InvestigationPlanner depend on ITransactionEventStore, so they must be
// Scoped too whenever that store is Scoped (EF-backed) — a Singleton may never capture a Scoped
// dependency (the DI container throws on this at startup with scope validation enabled). Scoped
// is safe in the in-memory case as well (it just means one instance per request instead of one
// for the app's lifetime, which costs nothing here since these are practically stateless).
// TransactionRiskEngine/DeviceRiskEngine/MerchantRiskEngine take all their input as method
// parameters rather than injected stores, so they can stay Singleton.
var semanticSection = builder.Configuration.GetSection("Intelligence:SemanticMemory");
var semanticEnabled = semanticSection.GetValue<bool>("Enabled");
if (semanticEnabled)
{
    var provider = semanticSection["Provider"];
    var model = semanticSection["Model"];
    var modelVersion = semanticSection["ModelVersion"];
    var dimensions = semanticSection.GetValue<int>("Dimensions");
    var endpoint = semanticSection["Endpoint"];
    var apiKey = semanticSection["ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (!string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Enabled semantic memory currently requires Provider=OpenAI.");
    if (string.IsNullOrWhiteSpace(model) || dimensions <= 0 || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("Enabled semantic memory requires Model, Dimensions, Endpoint and OPENAI_API_KEY (or secret configuration).");
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var embeddingEndpoint) || embeddingEndpoint.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("Semantic-memory Endpoint must be an absolute HTTPS URI.");

    builder.Services.AddHttpClient("semantic-embeddings", client =>
    {
        client.BaseAddress = embeddingEndpoint;
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<ITextEmbeddingService>(sp => new OpenAiTextEmbeddingService(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("semantic-embeddings"), apiKey, model, dimensions, modelVersion));
    builder.Services.AddScoped<IInvestigationMemory>(sp => new SemanticInvestigationMemory(
        sp.GetRequiredService<ITextEmbeddingService>(), sp.GetRequiredService<ISemanticCaseStore>(),
        new InMemoryInvestigationMemory(), semanticSection.GetValue("MinimumSimilarity", .2)));
}
else
{
    builder.Services.AddSingleton<IInvestigationMemory, InMemoryInvestigationMemory>();
}
builder.Services.AddSingleton<TransactionRiskEngine>(_ => new TransactionRiskEngine(
    new IAnomalyDetector[] { new TransactionAnomalyDetector(), new AmountAnomalyDetector(), new VelocityDetector() },
    new EvidenceCollector()));
builder.Services.AddSingleton<DeviceRiskEngine>();
builder.Services.AddSingleton<MerchantRiskEngine>();
builder.Services.AddScoped<InvestigationAgent>(sp => new InvestigationAgent(
    sp.GetRequiredService<ITransactionEventStore>(), sp.GetRequiredService<TransactionRiskEngine>()));
builder.Services.AddScoped<InvestigationPlanner>(sp => new InvestigationPlanner(
    sp.GetRequiredService<InvestigationAgent>(), sp.GetRequiredService<DeviceRiskEngine>()));
builder.Services.AddSingleton<MerchantInvestigationAgent>(sp => new MerchantInvestigationAgent(sp.GetRequiredService<MerchantRiskEngine>()));
builder.Services.AddScoped<InvestigationTools>();

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
    sp.GetRequiredService<IAuditRecordStore>(),
    sp.GetRequiredService<IPaymentAttemptStore>()));
builder.Services.AddScoped<AgentPurchaseOrchestrator>();
builder.Services.AddScoped<ConsumerPurchaseScheduler>();

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(connectionString))
{
    // Migrate(), not EnsureCreated(): a schema change now applies to a database that already
    // exists, instead of silently no-op'ing (EnsureCreated() only creates a schema for a
    // database that doesn't exist yet — see README for the bug this caused before migrations
    // existed: new tables from a later change never appeared in an already-created database).
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AgentTrustDbContext>().Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { } // exposed for WebApplicationFactory-based integration tests
