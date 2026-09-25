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
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using AgentTrust.Api.Authentication;
using AgentTrust.Api;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsEnvironment("Testing")) { builder.Logging.ClearProviders(); builder.Logging.AddConsole(); }
var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"] ?? Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH");
if (string.IsNullOrWhiteSpace(dataProtectionPath) && builder.Environment.IsEnvironment("Testing")) dataProtectionPath = Path.Combine(Path.GetTempPath(), "agenttrust-data-protection-tests");
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("AgentTrust");
if (!string.IsNullOrWhiteSpace(dataProtectionPath)) dataProtection.PersistKeysToFileSystem(Directory.CreateDirectory(dataProtectionPath));
var dataProtectionCertificate = builder.Configuration["DataProtection:CertificateThumbprint"] ?? Environment.GetEnvironmentVariable("DATA_PROTECTION_CERTIFICATE_THUMBPRINT");
if (!string.IsNullOrWhiteSpace(dataProtectionCertificate)) { using var certificateStore = new X509Store(StoreName.My, StoreLocation.CurrentUser); certificateStore.Open(OpenFlags.ReadOnly); var certificate = certificateStore.Certificates.Find(X509FindType.FindByThumbprint, dataProtectionCertificate, false).OfType<X509Certificate2>().SingleOrDefault() ?? throw new InvalidOperationException("The configured Data Protection certificate was not found."); dataProtection.ProtectKeysWithCertificate(certificate); }
else if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing") && !builder.Environment.IsEnvironment("E2E")) throw new InvalidOperationException("DATA_PROTECTION_CERTIFICATE_THUMBPRINT is required outside Development/Testing/E2E.");

builder.Services.AddControllers(options => options.InputFormatters.Insert(0, new TextPlainInputFormatter()));
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("agenttrust_principal_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var healthChecks = builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AgentTrust API",
        Version = "v1",
        Description = "Financial-agent intelligence and deterministic trust-boundary API."
    });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT access token only; Swagger adds the Bearer prefix."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        }] = Array.Empty<string>()
    });
});
var authority = builder.Configuration["Authentication:Authority"];
var audience = builder.Configuration["Authentication:Audience"];
var localTestEnvironment = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("E2E");
var developmentTokens = localTestEnvironment && builder.Configuration.GetValue("Authentication:Development:Enabled", false);
if (!localTestEnvironment && builder.Configuration.GetValue("Authentication:Development:Enabled", false))
    throw new InvalidOperationException("Local token authentication can run only in Development or E2E.");
if (!localTestEnvironment && !builder.Environment.IsEnvironment("Testing")
    && (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience)))
    throw new InvalidOperationException("Authentication:Authority and Authentication:Audience are required outside Development/Testing.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = developmentTokens ? null : authority;
    options.Audience = developmentTokens ? null : audience;
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
    options.MapInboundClaims = false;
    options.TokenValidationParameters = developmentTokens ? new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = "urn:agenttrust:development",
        ValidateAudience = true,
        ValidAudience = "agenttrust-development",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(
            builder.Configuration["Authentication:Development:SigningKey"] ?? throw new InvalidOperationException("Development SigningKey is required."))),
        RequireSignedTokens = true,
        ValidateLifetime = true,
        NameClaimType = "name",
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromSeconds(30)
    } : new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = authority ?? "urn:agenttrust:not-configured",
        ValidateAudience = true,
        ValidAudience = audience ?? "agenttrust-not-configured",
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        ValidateLifetime = true,
        NameClaimType = "name",
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromMinutes(2)
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Consumer", policy => policy.RequireAuthenticatedUser().AddRequirements(new StablePrincipalRequirement()));
    options.AddPolicy("StepUp", policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new StablePrincipalRequirement(), new StepUpRequirement()));
    options.AddPolicy("AuditAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("audit.read"));
});
builder.Services.AddScoped<IAuthorizationHandler, StablePrincipalHandler>();
builder.Services.AddScoped<IAuthorizationHandler, StepUpHandler>();

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
    healthChecks.AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
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

if (connectionString is not null)
{
    builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
    }).AddUserStore<EfApplicationUserStore>();
    builder.Services.AddScoped<IClaimsTransformation, ExternalIdentityClaimsTransformation>();
}

builder.Services.AddScoped<IPaymentAdapter, MockPaymentAdapter>();
if (connectionString is not null)
{
    builder.Services.AddScoped<IPaymentAttemptStore, EfPaymentAttemptStore>();
    builder.Services.AddScoped<IConsumerTaskStore, EfConsumerTaskStore>();
    builder.Services.AddScoped<IConnectedServiceStore, EfConnectedServiceStore>();
    builder.Services.AddScoped<IPurchaseExecutionStore, EfPurchaseExecutionStore>();
    builder.Services.AddScoped<IMandateStore, EfMandateStore>();
    builder.Services.AddScoped<IMandateLimitChangeStore, EfMandateLimitChangeStore>();
    builder.Services.AddScoped<IMandateUsageTracker, EfMandateUsageTracker>();
    builder.Services.AddScoped<IOneOffAuthorisationStore, EfOneOffAuthorisationStore>();
    builder.Services.AddScoped<IScheduledOccurrenceStore, EfScheduledOccurrenceStore>();
    builder.Services.AddScoped<IPaymentMethodStore, EfPaymentMethodStore>();
    builder.Services.AddScoped<IPurchaseAuditSink, EfPurchaseAuditSink>();
    builder.Services.AddScoped<ICommerceDurability, EfCommerceDurability>();
    builder.Services.AddScoped<IConsumerPlanningStore, EfConsumerPlanningStore>();
    builder.Services.AddScoped<IConsumerMemoryStore, EfConsumerMemoryStore>();
    builder.Services.AddScoped<IConsumerMemoryService, ConsumerMemoryService>();
    builder.Services.AddHostedService<ConsumerPilotWorker>();
}
else
{
    builder.Services.AddSingleton<IPaymentAttemptStore, InMemoryPaymentAttemptStore>();
    builder.Services.AddSingleton<IConsumerTaskStore, InMemoryConsumerTaskStore>();
    builder.Services.AddSingleton<IConnectedServiceStore, InMemoryConnectedServiceStore>();
    builder.Services.AddSingleton<IPurchaseExecutionStore, InMemoryPurchaseExecutionStore>();
    builder.Services.AddSingleton<IMandateStore, InMemoryMandateStore>();
    builder.Services.AddSingleton<IMandateLimitChangeStore, InMemoryMandateLimitChangeStore>();
    builder.Services.AddSingleton<IMandateUsageTracker, InMemoryMandateUsageTracker>();
    builder.Services.AddSingleton<IOneOffAuthorisationStore, InMemoryOneOffAuthorisationStore>();
    builder.Services.AddSingleton<IScheduledOccurrenceStore, InMemoryScheduledOccurrenceStore>();
    builder.Services.AddSingleton<IPaymentMethodStore, InMemoryPaymentMethodStore>();
    builder.Services.AddSingleton<IPurchaseAuditSink, InMemoryPurchaseAuditSink>();
    builder.Services.AddSingleton<ICommerceDurability, InMemoryCommerceDurability>();
    builder.Services.AddSingleton<IConsumerPlanningStore, InMemoryConsumerPlanningStore>();
    builder.Services.AddSingleton<IConsumerMemoryStore, InMemoryConsumerMemoryStore>();
    builder.Services.AddSingleton<IConsumerMemoryService, ConsumerMemoryService>();
}
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
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing")) throw new InvalidOperationException("PURCHASE_AUTHORISATION_KEY is required outside Development/Testing.");
    return new HmacPurchaseAuthorisationService(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
});
builder.Services.AddScoped<IPlatformPaymentProcessor>(sp =>
{
    var provider = builder.Configuration["Payments:Provider"] ?? "Mock";
    if (!provider.Equals("Stripe", StringComparison.OrdinalIgnoreCase)) return new MockPlatformPaymentProcessor();
    var mode = Enum.Parse<StripePaymentMode>(builder.Configuration["Payments:Mode"] ?? "Test", true);
    return new StripePaymentAdapter(builder.Configuration["Stripe:SecretKey"]
        ?? Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? "",
        new StripePaymentOptions(mode), sp.GetRequiredService<IPaymentMethodStore>());
});
builder.Services.AddScoped<DemoGroceryConnector>();
builder.Services.AddScoped<ICommerceConnector>(services => services.GetRequiredService<DemoGroceryConnector>());
builder.Services.AddScoped<MerchantConnectorRegistry>();
builder.Services.AddSingleton<IObjectiveExpansionCapability, GroceryMealObjectiveCapability>();
builder.Services.AddScoped<GroceryConsumerPurchasePlanner>();
builder.Services.AddScoped<ICommerceAnalystWorker, CommerceAnalystWorker>();
builder.Services.AddScoped<ICommercePlannerWorker, SemanticKernelCommerceAgentReasoningEngine>();
builder.Services.AddScoped<ICommerceAuditorWorker, SemanticKernelCommerceAuditorWorker>();
builder.Services.AddScoped<ConsumerCommerceAgentLoop>();
builder.Services.AddScoped<IProviderPlanningCapability, CommerceConnectorPlanningCapability>();
builder.Services.AddScoped<IDomainPlanningCapability, GroceryDomainPlanningCapability>();
builder.Services.AddScoped<IConsumerPurchaseRequestAgent, ConsumerPurchaseRequestAgent>();
builder.Services.AddScoped<ConsumerCommerceAgent>();
builder.Services.AddScoped<ConsumerCommerceOperator>();
builder.Services.AddScoped<ICustomerRequestUnderstandingAgent, SemanticKernelCustomerRequestUnderstandingAgent>();
builder.Services.AddSingleton<IServiceActionAuthorisationService>(_ =>
{
    var encoded = builder.Configuration["ServiceAuthorisation:Key"] ?? Environment.GetEnvironmentVariable("SERVICE_ACTION_AUTHORISATION_KEY");
    if (!string.IsNullOrWhiteSpace(encoded)) return new HmacServiceActionAuthorisationService(Convert.FromBase64String(encoded));
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing")) throw new InvalidOperationException("SERVICE_ACTION_AUTHORISATION_KEY is required outside Development/Testing.");
    return new HmacServiceActionAuthorisationService(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
});
builder.Services.AddScoped<IServiceConnector, DemoRestaurantConnector>();
builder.Services.AddSingleton<IExternalExecutionControl, ConfigurationExternalExecutionControl>();
if (connectionString is not null)
{
    builder.Services.AddScoped<IFulfilmentStore, EfFulfilmentStore>();
    builder.Services.AddScoped<IFulfilmentWebhookHandler>(services =>
        (EfFulfilmentStore)services.GetRequiredService<IFulfilmentStore>());
    builder.Services.AddHostedService<FulfilmentReconciliationWorker>();
}
else
{
    builder.Services.AddSingleton<IFulfilmentStore, InMemoryFulfilmentStore>();
    builder.Services.AddSingleton<IFulfilmentWebhookHandler>(services =>
        (InMemoryFulfilmentStore)services.GetRequiredService<IFulfilmentStore>());
}
builder.Services.AddScoped<DemoThirdPartyDeliveryConnector>();
builder.Services.AddScoped<IThirdPartyDeliveryConnector>(services => services.GetRequiredService<DemoThirdPartyDeliveryConnector>());
builder.Services.AddScoped<IFulfilmentReconciliationService>(services => services.GetRequiredService<DemoThirdPartyDeliveryConnector>());
builder.Services.AddScoped<IServiceConnector>(services => services.GetRequiredService<DemoThirdPartyDeliveryConnector>());
builder.Services.AddScoped<ServiceConnectorRegistry>();
builder.Services.AddScoped<IServiceDomainCapability, RestaurantDomainCapability>();
builder.Services.AddScoped<ServicePlanningRouter>();
builder.Services.AddScoped<MandateLimitChangeService>();

var consumerMemoryEnabled = builder.Configuration.GetValue("ConsumerMemory:Enabled", false);
if (consumerMemoryEnabled)
{
    if (connectionString is null) throw new InvalidOperationException("Consumer semantic memory requires the SQL system of record.");
    var qdrantUrl = builder.Configuration["ConsumerMemory:Qdrant:Url"] ?? throw new InvalidOperationException("ConsumerMemory:Qdrant:Url is required.");
    var redisConnection = builder.Configuration["ConsumerMemory:Redis:ConnectionString"] ?? throw new InvalidOperationException("ConsumerMemory:Redis:ConnectionString is required.");
    var embeddingModel = builder.Configuration["ConsumerMemory:Embedding:Model"] ?? "text-embedding-3-small"; var embeddingDimensions = builder.Configuration.GetValue("ConsumerMemory:Embedding:Dimensions", 1536);
    var embeddingEndpoint = builder.Configuration["ConsumerMemory:Embedding:Endpoint"] ?? "https://api.openai.com/v1/"; var embeddingKey = builder.Configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY is required for consumer semantic memory.");
    builder.Services.AddStackExchangeRedisCache(options => options.Configuration = redisConnection);
    builder.Services.AddHttpClient("consumer-memory-embeddings", client => { client.BaseAddress = new Uri(embeddingEndpoint); client.Timeout = TimeSpan.FromSeconds(30); });
    builder.Services.AddHttpClient<IConsumerMemoryVectorIndex, QdrantConsumerMemoryVectorIndex>(client => { client.BaseAddress = new Uri(qdrantUrl.TrimEnd('/') + "/"); client.Timeout = TimeSpan.FromSeconds(15); });
    builder.Services.AddScoped<IConsumerMemoryEmbeddingService>(sp => new ConsumerEmbeddingAdapter(new OpenAiTextEmbeddingService(sp.GetRequiredService<IHttpClientFactory>().CreateClient("consumer-memory-embeddings"), embeddingKey, embeddingModel, embeddingDimensions)));
    builder.Services.AddScoped<IConsumerMemoryCache, RedisConsumerMemoryCache>(); builder.Services.AddScoped<IConsumerMemoryRetriever>(sp => new SemanticConsumerMemoryRetriever(sp.GetRequiredService<IConsumerMemoryStore>(), sp.GetRequiredService<IConsumerMemoryEmbeddingService>(), sp.GetRequiredService<IConsumerMemoryVectorIndex>(), sp.GetRequiredService<IConsumerMemoryCache>(), TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue("ConsumerMemory:Redis:CacheTtlSeconds", 120), 10, 3600))));
    builder.Services.AddScoped<IConsumerMemoryOutboxStore, EfConsumerMemoryOutboxStore>(); builder.Services.AddHostedService<ConsumerMemoryIndexWorker>();
}

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

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (status, code, title) = exception switch
    {
        KeyNotFoundException => (StatusCodes.Status404NotFound, "RESOURCE_NOT_FOUND", "Resource not found"),
        InvalidOperationException e when e.Message.StartsWith("QUOTE_EXPIRED", StringComparison.Ordinal) => (StatusCodes.Status409Conflict, "QUOTE_EXPIRED", "Quote expired"),
        InvalidOperationException e when e.Message.StartsWith("IDEMPOTENCY_CONFLICT", StringComparison.Ordinal) => (StatusCodes.Status409Conflict, "DUPLICATE_REQUEST", "The idempotency key belongs to a different request"),
        InvalidOperationException e when e.Message.StartsWith("INVALID_STATE_TRANSITION", StringComparison.Ordinal) => (StatusCodes.Status409Conflict, "INVALID_STATE_TRANSITION", "Invalid state transition"),
        InvalidOperationException e when e.Message.StartsWith("EXECUTION_UNKNOWN", StringComparison.Ordinal) => (StatusCodes.Status503ServiceUnavailable, "RECONCILIATION_REQUIRED", "The provider outcome is being reconciled"),
        _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "The request could not be completed")
    };
    context.Response.StatusCode = status;
    await Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);
}));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AgentTrust API v1");
        options.RoutePrefix = "swagger";
        options.DisplayRequestDuration();
        options.EnableTryItOutByDefault();
    });
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();
app.Run();

public partial class Program { } // exposed for WebApplicationFactory-based integration tests
