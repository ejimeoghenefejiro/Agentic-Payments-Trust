using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentTrust.Intelligence.Investigation;
using Xunit;

namespace AgentTrust.Tests;

/// <summary>
/// End-to-end HTTP tests against AgentTrust.Api hosted in-process via WebApplicationFactory.
///
/// WebApplicationFactory defaults to the "Development" environment, which — on a developer
/// machine that has ever configured a local connection string in appsettings.Development.json
/// (e.g. for manual SQL Server testing) — means the test host would silently pick that up and
/// hit a real database instead of the in-memory stores these tests assume. That's exactly what
/// happened once: the two intelligence tests below started failing with 500s the moment
/// appsettings.Development.json gained a real SqlServer connection string, because
/// Database.EnsureCreated() had already created that database on an earlier schema and doesn't
/// retroactively add new tables to it. Forcing an explicit "Testing" environment (no
/// appsettings.Testing.json exists, so nothing extra loads) and blanking both connection-string
/// keys makes this test class hermetic regardless of what's configured on the machine running it.
/// PersistenceTests separately proves the EF-Core mapping against a real relational engine
/// (SQLite), so the trust-layer and intelligence EF stores are still exercised for real.
/// </summary>
public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var hermeticFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlServer"] = null,
                ["ConnectionStrings:Postgres"] = null
            }));
            builder.ConfigureServices(services => services.AddAuthentication("IntegrationTest")
                .AddScheme<AuthenticationSchemeOptions, IntegrationTestAuthHandler>("IntegrationTest", _ => { }));
        });
        _factory = hermeticFactory;
        _client = hermeticFactory.CreateClient();
    }

    [Fact]
    public async Task ConsumerRoutesRejectRequestsWithoutValidatedBearerIdentity()
    {
        var response = await _client.GetAsync("/api/consumer/tasks");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NewConsumerReceivesOrderedSetupRequirementsBeforeAgentPlanning()
    {
        _client.DefaultRequestHeaders.Add("X-Test-Principal",$"new_consumer_{Guid.NewGuid():N}");_client.DefaultRequestHeaders.Add("X-Test-Mfa","true");
        var status=await _client.GetFromJsonAsync<JsonElement>("/api/consumer/setup/status");
        Assert.False(status.GetProperty("isReady").GetBoolean());var steps=status.GetProperty("requiredSetupSteps");Assert.Equal(3,steps.GetArrayLength());
        Assert.Equal("AGENT",steps[0].GetProperty("resource").GetString());Assert.Equal("PAYMENT_METHOD",steps[1].GetProperty("resource").GetString());Assert.Equal("MANDATE",steps[2].GetProperty("resource").GetString());
        using var body=new StringContent("I want to make chicken wraps. Get all required ingredients within a £20 budget.",System.Text.Encoding.UTF8,"text/plain");
        var response=await _client.PostAsync("/api/consumer/purchases/request",body);Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }

    [Fact]
    public async Task ConsumerSwaggerPath_CreatesOwnedMandateTaskPurchaseReceiptAndAudit_Idempotently()
    {
        var suffix=Guid.NewGuid().ToString("N")[..8];var principal=$"consumer_{suffix}";var agent=$"consumer_agent_{suffix}";
        _client.DefaultRequestHeaders.Add("X-Test-Principal",principal);_client.DefaultRequestHeaders.Add("X-Test-Mfa","true");
        Assert.Equal(HttpStatusCode.Created,(await _client.PostAsJsonAsync("/api/consumer/agents",new{agentId=agent,displayName="Test consumer"})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await _client.PostAsJsonAsync("/api/consumer/agents",new{agentId=agent,displayName="Test consumer"})).StatusCode);
        var methodResponse=await _client.PostAsJsonAsync("/api/consumer/payment-methods/setup",new{provider="Stripe",providerToken=$"pm_test_{suffix}",cardBrand="visa",last4="4242",expiryMonth=12,expiryYear=2035});
        Assert.Equal(HttpStatusCode.OK,methodResponse.StatusCode);var method=await methodResponse.Content.ReadFromJsonAsync<JsonElement>();var methodId=method.GetProperty("paymentMethodId").GetString()!;
        var repeatedMethod=await _client.PostAsJsonAsync("/api/consumer/payment-methods/setup",new{provider="Stripe",providerToken=$"pm_test_{suffix}",cardBrand="visa",last4="4242",expiryMonth=12,expiryYear=2035});
        Assert.Equal(HttpStatusCode.OK,repeatedMethod.StatusCode);Assert.Equal(methodId,(await repeatedMethod.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentMethodId").GetString());
        var now=DateTimeOffset.UtcNow;
        var mandateResponse=await _client.PostAsJsonAsync("/api/consumer/mandates",new{agentId=agent,merchantIds=new[]{"demo-grocery"},paymentMethodId=methodId,currency="GBP",perTransactionLimit=70m,weeklyLimit=70m,humanApprovalThreshold=70m,validFrom=now.AddMinutes(-1),validUntil=now.AddMonths(1)});
        Assert.Equal(HttpStatusCode.Created,mandateResponse.StatusCode);var mandate=await mandateResponse.Content.ReadFromJsonAsync<JsonElement>();var mandateId=mandate.GetProperty("mandateId").GetString()!;
        using(var lowBudgetRequest=new StringContent("I want to make chicken wraps. Get all required ingredients within a £4.99 budget.",System.Text.Encoding.UTF8,"text/plain"))
        {
            var lowBudgetResponse=await _client.PostAsync("/api/consumer/purchases/request",lowBudgetRequest);Assert.Equal(HttpStatusCode.OK,lowBudgetResponse.StatusCode);
            var lowBudget=await lowBudgetResponse.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("NEEDS_INPUT",lowBudget.GetProperty("planning").GetProperty("status").GetString());
            Assert.False(lowBudget.GetProperty("paymentAttempted").GetBoolean());Assert.False(lowBudget.GetProperty("trustBoundaryInvoked").GetBoolean());Assert.False(lowBudget.TryGetProperty("taskId",out _));
        }
        using(var naturalRequest=new StringContent("I want to make chicken wraps. Get all required ingredients within a £20 budget.",System.Text.Encoding.UTF8,"text/plain"))
        {
            var naturalResponse=await _client.PostAsync("/api/consumer/purchases/request",naturalRequest);
            Assert.Equal(HttpStatusCode.OK,naturalResponse.StatusCode);var natural=await naturalResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Chicken wraps",natural.GetProperty("planning").GetProperty("summary").GetString());
            Assert.Contains("price_basket",natural.GetProperty("planning").GetProperty("toolsUsed").EnumerateArray().Select(x=>x.GetString()));
            Assert.Equal((int)AgentTrust.Consumer.PurchaseExecutionState.Purchased,natural.GetProperty("execution").GetProperty("state").GetInt32());
            Assert.Equal(5,natural.GetProperty("intent").GetProperty("basketItems").GetArrayLength());
        }
        var taskResponse=await _client.PostAsJsonAsync("/api/consumer/tasks",new{instruction="Buy weekly groceries",merchantId="demo-grocery",mandateId,paymentMethodId=methodId,currency="GBP",maximumAmount=70m,timezone="Europe/London",schedule=new{frequency="Weekly",dayOfWeek="Sunday",localTime="10:00"},shoppingList=new[]{new{query="milk",quantity=2},new{query="bread",quantity=1},new{query="eggs",quantity=1},new{query="bananas",quantity=1}},substitutionPolicy=new{allowed=true,maximumAdditionalAmount=5m},deliveryAddressReference="test-address"});
        Assert.Equal(HttpStatusCode.Created,taskResponse.StatusCode);var task=await taskResponse.Content.ReadFromJsonAsync<JsonElement>();var taskId=task.GetProperty("taskId").GetString()!;
        var scheduled=DateTimeOffset.Parse("2030-01-06T10:00:00Z");
        var first=await _client.PostAsJsonAsync($"/api/consumer/tasks/{taskId}/run",new{scheduledFor=scheduled,liveMode=false,explicitLiveConfirmation=false});
        Assert.Equal(HttpStatusCode.OK,first.StatusCode);var firstBody=await first.Content.ReadFromJsonAsync<JsonElement>();var execution=firstBody.GetProperty("execution");var executionId=execution.GetProperty("executionId").GetString()!;
        Assert.True(execution.GetProperty("state").GetInt32()==(int)AgentTrust.Consumer.PurchaseExecutionState.Purchased,firstBody.ToString());
        var second=await _client.PostAsJsonAsync($"/api/consumer/tasks/{taskId}/run",new{scheduledFor=scheduled,liveMode=false,explicitLiveConfirmation=false});
        var secondBody=await second.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(executionId,secondBody.GetProperty("execution").GetProperty("executionId").GetString());
        Assert.Equal(HttpStatusCode.OK,(await _client.GetAsync($"/api/consumer/purchases/{executionId}/receipt")).StatusCode);
        var audit=await _client.GetFromJsonAsync<JsonElement>($"/api/consumer/purchases/{executionId}/audit");Assert.True(audit.GetProperty("isValid").GetBoolean());Assert.True(audit.GetProperty("eventCount").GetInt32()>=8);
        using var outsider=_factory.CreateClient();outsider.DefaultRequestHeaders.Add("X-Test-Principal",$"other_{suffix}");
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"/api/consumer/purchases/{executionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"/api/consumer/purchases/{executionId}/receipt")).StatusCode);
    }

    [Fact]
    public void SemanticMemoryIsLexicalWhenProviderIsNotExplicitlyEnabled()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.IsType<InMemoryInvestigationMemory>(scope.ServiceProvider.GetRequiredService<IInvestigationMemory>());
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ITextEmbeddingService>());
    }

    [Fact]
    public void SemanticMemoryActivatesOnlyWithExplicitCompleteConfiguration()
    {
        using var semanticFactory = _factory.WithWebHostBuilder(builder => builder
            .UseSetting("Intelligence:SemanticMemory:Enabled", "true")
            .UseSetting("Intelligence:SemanticMemory:Provider", "OpenAI")
            .UseSetting("Intelligence:SemanticMemory:Model", "test-embedding-model")
            .UseSetting("Intelligence:SemanticMemory:Dimensions", "3")
            .UseSetting("Intelligence:SemanticMemory:Endpoint", "https://embeddings.example/v1/")
            .UseSetting("Intelligence:SemanticMemory:ApiKey", "runtime-test-secret"));
        using var scope = semanticFactory.Services.CreateScope();

        Assert.IsType<SemanticInvestigationMemory>(scope.ServiceProvider.GetRequiredService<IInvestigationMemory>());
        var embeddings = Assert.IsType<AgentTrust.Agents.OpenAiTextEmbeddingService>(scope.ServiceProvider.GetRequiredService<ITextEmbeddingService>());
        Assert.Equal("test-embedding-model", embeddings.Model);
    }

    [Fact]
    public async Task FullLifecycle_RegisterAgent_GrantAuthority_ApproveTransaction_VerifyAudit()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agentId = $"agt_api_{suffix}";
        var principalId = $"org_api_{suffix}";
        var authorityId = $"auth_api_{suffix}";
        var txId = $"tx_api_{suffix}";

        var registerPrincipal = await _client.PostAsJsonAsync("/api/principals", new { principalId, name = "API Test Org" });
        Assert.Equal(HttpStatusCode.Created, registerPrincipal.StatusCode);

        var registerAgent = await _client.PostAsJsonAsync("/api/agents", new { agentId, principalId, agentType = "procurement", environment = "production" });
        Assert.Equal(HttpStatusCode.Created, registerAgent.StatusCode);

        var getAgent = await _client.GetAsync($"/api/agents/{agentId}");
        Assert.Equal(HttpStatusCode.OK, getAgent.StatusCode);

        var bind = await _client.PostAsJsonAsync("/api/bindings", new { agentId, principalId, bindingEvidenceRef = "doc_1" });
        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);

        var grantAuthority = await _client.PostAsJsonAsync("/api/authorities", new
        {
            authorityId,
            agentId,
            permissions = new[] { "purchase:fuel" },
            perTransactionLimit = 50000,
            dailyLimit = 200000,
            approvedMerchants = new[] { "ABC Energy" },
            categoryScope = new[] { "fuel" },
            geographicScope = "NG",
            humanApprovalAbove = 40000,
            expiry = "2027-12-31"
        });
        Assert.Equal(HttpStatusCode.Created, grantAuthority.StatusCode);

        var getAuthority = await _client.GetAsync($"/api/authorities/{authorityId}");
        Assert.Equal(HttpStatusCode.OK, getAuthority.StatusCode);

        var submitTransaction = await _client.PostAsJsonAsync("/api/transactions/request", new
        {
            transactionId = txId,
            agentId,
            principalId,
            userInstruction = (string?)null,
            expectedCurrency = "NGN",
            action = "purchase:fuel",
            merchant = "ABC Energy",
            category = "fuel",
            amount = 20000,
            reason = "fuel_sensor_below_threshold",
            idempotencyKey = txId,
            evidence = new[] { new { evidenceId = "ev_1", type = "sensor_reading", description = "reading", exists = true } },
            context = (Dictionary<string, string>?)null,
            scriptedAgentResponse = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, submitTransaction.StatusCode);
        var body = await submitTransaction.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approve", body.GetProperty("decision").GetString());

        var getTransaction = await _client.GetAsync($"/api/transactions/{txId}");
        Assert.Equal(HttpStatusCode.OK, getTransaction.StatusCode);

        _client.DefaultRequestHeaders.Add("X-Test-Principal", principalId);
        _client.DefaultRequestHeaders.Add("X-Test-Roles", "audit.read");
        var auditGet = await _client.GetAsync($"/api/audit/{txId}");
        Assert.Equal(HttpStatusCode.OK, auditGet.StatusCode);

        var verify = await _client.GetAsync("/api/audit/verify");
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verifyBody = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(verifyBody.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task EscalatedTransaction_RequiresApprovalBeforePaymentExecutes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agentId = $"agt_esc_{suffix}";
        var principalId = $"org_esc_{suffix}";
        var authorityId = $"auth_esc_{suffix}";
        var txId = $"tx_esc_{suffix}";

        await _client.PostAsJsonAsync("/api/principals", new { principalId, name = "Escalation Org" });
        await _client.PostAsJsonAsync("/api/agents", new { agentId, principalId, agentType = "procurement", environment = "production" });
        await _client.PostAsJsonAsync("/api/bindings", new { agentId, principalId, bindingEvidenceRef = "doc_1" });
        await _client.PostAsJsonAsync("/api/authorities", new
        {
            authorityId,
            agentId,
            permissions = new[] { "purchase:fuel" },
            perTransactionLimit = 50000,
            dailyLimit = 200000,
            approvedMerchants = new[] { "ABC Energy" },
            categoryScope = new[] { "fuel" },
            geographicScope = "NG",
            humanApprovalAbove = 40000,
            expiry = "2027-12-31"
        });

        var submit = await _client.PostAsJsonAsync("/api/transactions/request", new
        {
            transactionId = txId,
            agentId,
            principalId,
            userInstruction = (string?)null,
            expectedCurrency = "NGN",
            action = "purchase:fuel",
            merchant = "ABC Energy",
            category = "fuel",
            amount = 45000,
            reason = "large purchase",
            idempotencyKey = txId,
            evidence = new[] { new { evidenceId = "ev_1", type = "sensor_reading", description = "reading", exists = true } },
            context = (Dictionary<string, string>?)null,
            scriptedAgentResponse = (string?)null
        });
        var submitBody = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Escalate", submitBody.GetProperty("decision").GetString());
        Assert.Equal("NotAttempted", submitBody.GetProperty("paymentStatus").GetString());

        _client.DefaultRequestHeaders.Add("X-Test-Principal", principalId);
        _client.DefaultRequestHeaders.Add("X-Test-Mfa", "true");
        var approve = await _client.PostAsJsonAsync($"/api/approvals/{txId}", new { approve = true, approver = "supervisor@example.com", reason = "confirmed" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approveBody = await approve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approve", approveBody.GetProperty("finalDecision").GetString());
        Assert.Equal("Success", approveBody.GetProperty("paymentStatus").GetString());

        var secondApprove = await _client.PostAsJsonAsync($"/api/approvals/{txId}", new { approve = true, approver = "someone_else@example.com", reason = "duplicate attempt" });
        Assert.Equal(HttpStatusCode.Conflict, secondApprove.StatusCode);
    }

    private sealed class IntegrationTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public IntegrationTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Principal", out var principal))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(AgentTrust.Api.Authentication.AgentTrustClaimTypes.PrincipalId, principal.ToString()) };
            if (Request.Headers["X-Test-Mfa"] == "true")
            { claims.Add(new("amr", "mfa")); claims.Add(new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())); }
            foreach (var role in Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)) claims.Add(new(ClaimTypes.Role, role));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    [Fact]
    public async Task IntelligenceInvestigate_FlagsANightTimeAnomalyAfterHistoryIsRecorded()
    {
        var customerId = $"C_api_{Guid.NewGuid():N}"[..16];

        for (var i = 0; i < 25; i++)
        {
            var recorded = await _client.PostAsJsonAsync("/api/intelligence/events", new
            {
                transactionId = $"tx_hist_{i}",
                customerId,
                merchantId = "M14",
                amount = 100 + i,
                currency = "GBP",
                timestamp = DateTimeOffset.Parse("2027-05-01T12:00:00Z").AddDays(i),
                deviceId = "D44",
                ipAddress = "1.2.3.4",
                location = "Manchester",
                beneficiaryId = "B101",
                beneficiaryCreatedAt = (DateTimeOffset?)null,
                wasRefunded = false,
                priorFailedAttempts = 0
            });
            Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        }

        var profile = await _client.GetAsync($"/api/intelligence/customers/{customerId}/profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var profileBody = await profile.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(25, profileBody.GetProperty("sampleSize").GetInt32());

        var investigate = await _client.PostAsJsonAsync("/api/intelligence/investigate", new
        {
            transactionId = "tx_risky",
            customerId,
            merchantId = "M14",
            amount = 9000,
            currency = "GBP",
            timestamp = DateTimeOffset.Parse("2027-06-25T03:41:00Z"),
            deviceId = "D999-unknown",
            ipAddress = "203.0.113.9",
            location = "Lagos",
            beneficiaryId = "B999-new",
            beneficiaryCreatedAt = DateTimeOffset.Parse("2027-06-25T03:39:00Z"),
            wasRefunded = false,
            priorFailedAttempts = 3
        });
        Assert.Equal(HttpStatusCode.OK, investigate.StatusCode);
        var investigateBody = await investigate.Content.ReadFromJsonAsync<JsonElement>();
        var finalAssessment = investigateBody.GetProperty("finalAssessment");
        Assert.True(finalAssessment.GetProperty("riskScore").GetInt32() >= 50);
        Assert.True(finalAssessment.GetProperty("riskFactors").GetArrayLength() >= 5);
    }

    [Fact]
    public async Task TransactionRequest_WithCandidateEvent_CarriesIntelligenceAlongsideAnIndependentTrustDecision()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agentId = $"agt_intel_{suffix}";
        var principalId = $"org_intel_{suffix}";
        var authorityId = $"auth_intel_{suffix}";
        var txId = $"tx_intel_{suffix}";

        await _client.PostAsJsonAsync("/api/principals", new { principalId, name = "Intelligence-Wired Org" });
        await _client.PostAsJsonAsync("/api/agents", new { agentId, principalId, agentType = "consumer", environment = "production" });
        await _client.PostAsJsonAsync("/api/bindings", new { agentId, principalId, bindingEvidenceRef = "doc_1" });
        await _client.PostAsJsonAsync("/api/authorities", new
        {
            authorityId,
            agentId,
            permissions = new[] { "purchase:fuel" },
            perTransactionLimit = 50000,
            dailyLimit = 200000,
            approvedMerchants = new[] { "M14" },
            categoryScope = new[] { "fuel" },
            geographicScope = "NG",
            humanApprovalAbove = 1000, // deliberately low so 9000 escalates on the trust layer's own grounds
            expiry = "2027-12-31"
        });

        var submit = await _client.PostAsJsonAsync("/api/transactions/request", new
        {
            transactionId = txId,
            agentId,
            principalId,
            userInstruction = (string?)null,
            expectedCurrency = "GBP",
            action = "purchase:fuel",
            merchant = "M14",
            category = "fuel",
            amount = 9000,
            reason = "night purchase",
            idempotencyKey = txId,
            evidence = Array.Empty<object>(),
            context = (Dictionary<string, string>?)null,
            scriptedAgentResponse = (string?)null,
            candidateEvent = new
            {
                transactionId = txId,
                customerId = $"C_new_{suffix}", // no prior history recorded for this customer
                merchantId = "M14",
                amount = 9000,
                currency = "GBP",
                timestamp = DateTimeOffset.Parse("2027-06-25T03:41:00Z"),
                deviceId = "D999",
                ipAddress = "203.0.113.9",
                location = "Lagos",
                beneficiaryId = "B999",
                beneficiaryCreatedAt = DateTimeOffset.Parse("2027-06-25T03:39:00Z"),
                wasRefunded = false,
                priorFailedAttempts = 3
            }
        });

        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();

        // The trust layer escalates on its own £1,000 human-approval threshold, independently of
        // whatever the intelligence layer recommends — a brand-new customer with no history gets
        // a low-confidence "Approve" from Intelligence (nothing to compare against yet), proving
        // neither layer defers to the other.
        Assert.Equal("Escalate", body.GetProperty("decision").GetString());
        Assert.Contains("HUMAN_APPROVAL_REQUIRED", body.GetProperty("reasonCodes").EnumerateArray().Select(r => r.GetString()));
        var intelligence = body.GetProperty("intelligence");
        Assert.Equal("Approve", intelligence.GetProperty("recommendation").GetString());

        // Intelligence evidence is advisory and must not be promoted into the authoritative
        // policy evidence manifest merely because an AI/analytics component emitted it.
        var stored = await _client.GetFromJsonAsync<JsonElement>($"/api/transactions/{txId}");
        Assert.Equal(0, stored.GetProperty("intent").GetProperty("evidence").GetArrayLength());
    }

    [Fact]
    public async Task IntelligenceFeedbackAndModelEvaluation_RoundTrip()
    {
        var txId = $"tx_fb_{Guid.NewGuid():N}";
        var recorded = await _client.PostAsJsonAsync("/api/intelligence/feedback", new
        {
            transactionId = txId,
            aiRecommendation = "Escalate",
            actualOutcome = "Suspicious",
            notes = "confirmed"
        });
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);

        var invalid = await _client.PostAsJsonAsync("/api/intelligence/feedback", new
        {
            transactionId = "tx_bad",
            aiRecommendation = "not-a-real-value",
            actualOutcome = "Legitimate",
            notes = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var evaluation = await _client.GetAsync("/api/intelligence/model-evaluation");
        Assert.Equal(HttpStatusCode.OK, evaluation.StatusCode);
        var evaluationBody = await evaluation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(evaluationBody.GetProperty("totalCases").GetInt32() >= 1);
    }
}
